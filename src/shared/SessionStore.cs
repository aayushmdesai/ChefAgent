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

    // ── Profile (stub — wired in Month 2) ────────────────────

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
}
