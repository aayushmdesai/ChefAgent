using System.Text.Json;
using ChefAgent.Shared.Models;
using StackExchange.Redis;

namespace ChefAgent.Shared;

public class SessionStore
{
    private readonly IDatabase _db;
    private static readonly TimeSpan DefaultTTL = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public SessionStore(IConnectionMultiplexer redis)
    {
        _db = redis.GetDatabase();
    }

    // ── History ───────────────────────────────────────────────

    private const int MaxHistoryEntries = 20;

    public async Task AppendMessageAsync(string sessionId, ConversationEntry entry)
    {
        var key = HistoryKey(sessionId);
        var json = JsonSerializer.Serialize(entry, JsonOptions);

        // Redis list — new messages pushed to the right
        await _db.ListRightPushAsync(key, json);

        // Trim to sliding window — keep only the last MaxHistoryEntries
        await _db.ListTrimAsync(key, -MaxHistoryEntries, -1);

        // Refresh TTL on every append so active sessions don't expire mid-conversation
        await _db.KeyExpireAsync(key, DefaultTTL);
    }

    public async Task<List<ConversationEntry>> GetHistoryAsync(string sessionId, int limit = 10)
    {
        var key = HistoryKey(sessionId);

        // Fetch from the right (most recent) — LRANGE with negative indices
        var entries = await _db.ListRangeAsync(key, -limit, -1);

        if (entries.Length == 0)
            return [];

        var result = new List<ConversationEntry>(entries.Length);
        foreach (var entry in entries)
        {
            if (entry.IsNull)
                continue;
            var deserialized = JsonSerializer.Deserialize<ConversationEntry>(entry!, JsonOptions);
            if (deserialized is not null)
                result.Add(deserialized);
        }
        return result;
    }

    // ── Plan ─────────────────────────────────────────────────

    public async Task SavePlanAsync(string sessionId, MealPlan plan)
    {
        var key = PlanKey(sessionId);
        var json = JsonSerializer.Serialize(plan, JsonOptions);
        await _db.StringSetAsync(key, json, DefaultTTL);
    }

    public async Task<MealPlan?> GetPlanAsync(string sessionId)
    {
        var key = PlanKey(sessionId);
        var json = await _db.StringGetAsync(key);
        if (json.IsNull)
            return null;
        return JsonSerializer.Deserialize<MealPlan>(json!, JsonOptions);
    }

    public async Task DeletePlanAsync(string sessionId)
    {
        await _db.KeyDeleteAsync(PlanKey(sessionId));
    }

    public async Task SaveProfileAsync(string sessionId, DietaryProfile profile)
    {
        var key = ProfileKey(sessionId);
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        await _db.StringSetAsync(key, json, DefaultTTL);
    }

    public async Task<DietaryProfile?> GetProfileAsync(string sessionId)
    {
        var key = ProfileKey(sessionId);
        var json = await _db.StringGetAsync(key);
        if (json.IsNull)
            return null;
        return JsonSerializer.Deserialize<DietaryProfile>(json!, JsonOptions);
    }

    // ── Key helpers ───────────────────────────────────────────

    private static string PlanKey(string sessionId) => $"session:{sessionId}:plan";

    private static string ProfileKey(string sessionId) => $"session:{sessionId}:profile";

    private static string HistoryKey(string sessionId) => $"session:{sessionId}:history";
}
