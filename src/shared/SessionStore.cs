using System.Text.Json;
using ChefAgent.Shared.Guardrails;
using ChefAgent.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace ChefAgent.Shared;

public class SessionStore
{
    private readonly IDatabase _db;
    private static readonly TimeSpan DefaultTTL = TimeSpan.FromDays(7);
    private readonly CircuitBreaker _redisCircuitBreaker;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public SessionStore(
        IConnectionMultiplexer redis,
        [FromKeyedServices("redis")] CircuitBreaker redisCircuitBreaker
    )
    {
        _db = redis.GetDatabase();
        _redisCircuitBreaker = redisCircuitBreaker;
    }

    // ── History ───────────────────────────────────────────────

    private const int MaxHistoryEntries = 20;

    public async Task AppendMessageAsync(string sessionId, ConversationEntry entry)
    {
        if (!_redisCircuitBreaker.IsAllowed())
            return;
        try
        {
            var key = HistoryKey(sessionId);
            var json = JsonSerializer.Serialize(entry, JsonOptions);
            // Redis list — new messages pushed to the right
            await _db.ListRightPushAsync(key, json);
            // Trim to sliding window — keep only the last MaxHistoryEntries
            await _db.ListTrimAsync(key, -MaxHistoryEntries, -1);
            // Refresh TTL on every append so active sessions don't expire mid-conversation
            await _db.KeyExpireAsync(key, DefaultTTL);
            _redisCircuitBreaker.RecordSuccess();
        }
        catch (Exception)
        {
            _redisCircuitBreaker.RecordFailure();
            // non-critical — history loss is acceptable
        }
    }

    public async Task<List<ConversationEntry>> GetHistoryAsync(string sessionId, int limit = 10)
    {
        if (!_redisCircuitBreaker.IsAllowed())
            return [];
        try
        {
            var key = HistoryKey(sessionId);
            var entries = await _db.ListRangeAsync(key, -limit, -1);
            _redisCircuitBreaker.RecordSuccess();

            if (entries.Length == 0)
                return [];

            var result = new List<ConversationEntry>(entries.Length);
            foreach (var entry in entries)
            {
                if (entry.IsNull)
                    continue;
                var deserialized = JsonSerializer.Deserialize<ConversationEntry>(
                    entry!,
                    JsonOptions
                );
                if (deserialized is not null)
                    result.Add(deserialized);
            }
            return result;
        }
        catch (Exception)
        {
            _redisCircuitBreaker.RecordFailure();
            return [];
        }
    }

    // ── Plan ─────────────────────────────────────────────────

    public async Task SavePlanAsync(string sessionId, MealPlan plan)
    {
        if (!_redisCircuitBreaker.IsAllowed())
            return;
        try
        {
            var key = PlanKey(sessionId);
            var json = JsonSerializer.Serialize(plan, JsonOptions);
            await _db.StringSetAsync(key, json, DefaultTTL);
            _redisCircuitBreaker.RecordSuccess();
        }
        catch (Exception)
        {
            _redisCircuitBreaker.RecordFailure();
        }
    }

    public async Task<MealPlan?> GetPlanAsync(string sessionId)
    {
        if (!_redisCircuitBreaker.IsAllowed())
            return null;
        try
        {
            var key = PlanKey(sessionId);
            var json = await _db.StringGetAsync(key);
            _redisCircuitBreaker.RecordSuccess();
            if (json.IsNull)
                return null;
            return JsonSerializer.Deserialize<MealPlan>(json!, JsonOptions);
        }
        catch (Exception)
        {
            _redisCircuitBreaker.RecordFailure();
            return null;
        }
    }

    public async Task DeletePlanAsync(string sessionId)
    {
        if (!_redisCircuitBreaker.IsAllowed())
            return;
        try
        {
            await _db.KeyDeleteAsync(PlanKey(sessionId));
            _redisCircuitBreaker.RecordSuccess();
        }
        catch (Exception)
        {
            _redisCircuitBreaker.RecordFailure();
        }
    }

    // ── Profile ───────────────────────────────────────────────

    public async Task SaveProfileAsync(string sessionId, DietaryProfile profile)
    {
        if (!_redisCircuitBreaker.IsAllowed())
            return;
        try
        {
            var key = ProfileKey(sessionId);
            var json = JsonSerializer.Serialize(profile, JsonOptions);
            await _db.StringSetAsync(key, json, DefaultTTL);
            _redisCircuitBreaker.RecordSuccess();
        }
        catch (Exception)
        {
            _redisCircuitBreaker.RecordFailure();
        }
    }

    public async Task<DietaryProfile?> GetProfileAsync(string sessionId)
    {
        if (!_redisCircuitBreaker.IsAllowed())
            return null;
        try
        {
            var key = ProfileKey(sessionId);
            var json = await _db.StringGetAsync(key);
            _redisCircuitBreaker.RecordSuccess();
            if (json.IsNull)
                return null;
            return JsonSerializer.Deserialize<DietaryProfile>(json!, JsonOptions);
        }
        catch (Exception)
        {
            _redisCircuitBreaker.RecordFailure();
            return null;
        }
    }

    // ── Extracted Profile Cache ───────────────────────────────

    public async Task SetCachedExtractionAsync(string sessionId, DietaryProfile? profile)
    {
        if (!_redisCircuitBreaker.IsAllowed())
            return;
        try
        {
            var key = ExtractionKey(sessionId);
            // Cache null as empty JSON object — means "extraction ran, found nothing"
            var json = profile is null ? "{}" : JsonSerializer.Serialize(profile, JsonOptions);
            await _db.StringSetAsync(key, json, DefaultTTL);
            _redisCircuitBreaker.RecordSuccess();
        }
        catch (Exception)
        {
            _redisCircuitBreaker.RecordFailure();
        }
    }

    public async Task<(bool ranBefore, DietaryProfile? profile)> GetCachedExtractionAsync(
        string sessionId
    )
    {
        if (!_redisCircuitBreaker.IsAllowed())
            return (false, null); // treat as never ran — allow LLM to fire
        try
        {
            var key = ExtractionKey(sessionId);
            var json = await _db.StringGetAsync(key);
            _redisCircuitBreaker.RecordSuccess();

            if (json.IsNull)
                return (false, null); // never ran

            if (json == "{}")
                return (true, null); // ran, found nothing

            var profile = JsonSerializer.Deserialize<DietaryProfile>(json!, JsonOptions);
            return (true, profile); // ran, found something
        }
        catch (Exception)
        {
            _redisCircuitBreaker.RecordFailure();
            return (false, null);
        }
    }

    // ── Key helpers ───────────────────────────────────────────

    private static string PlanKey(string sessionId) => $"session:{sessionId}:plan";

    private static string ProfileKey(string sessionId) => $"session:{sessionId}:profile";

    private static string HistoryKey(string sessionId) => $"session:{sessionId}:history";

    private static string ExtractionKey(string sessionId) => $"session:{sessionId}:extraction";
}
