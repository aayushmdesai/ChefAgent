using System.Text.Json;
using ChefAgent.Shared.Models;
using Microsoft.Extensions.Logging;

namespace ChefAgent.Shared;

public class OutputGuard
{
    private readonly ILogger<OutputGuard> _logger;
    private readonly GuardrailAuditLog _audit;
    private int _llmRetryCount;
    private int _llmFallbackCount;
    private int _recipesSanitized;

    public int LlmRetryCount => _llmRetryCount;
    public int LlmFallbackCount => _llmFallbackCount;
    public int RecipesSanitized => _recipesSanitized;

    public OutputGuard(ILogger<OutputGuard> logger, GuardrailAuditLog audit)
    {
        _logger = logger;
        _audit = audit;
    }

    // ── 1. JSON extraction + schema validation ───────────────────────

    /// <summary>
    /// Extracts and deserializes JSON from LLM text output.
    /// Handles markdown fences, trailing explanation text, and malformed responses.
    /// Returns null if extraction or deserialization fails.
    /// </summary>
    public T? TryParseJson<T>(string? raw, string callerContext)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            _logger.LogWarning("[OutputGuard] Empty LLM response in {Context}", callerContext);
            return null;
        }

        // Strip markdown fences
        var cleaned = raw.Replace("```json", "").Replace("```", "").Trim();

        // Extract first JSON object or array
        var start = cleaned.IndexOfAny(['{', '[']);
        if (start < 0)
        {
            _logger.LogWarning(
                "[OutputGuard] No JSON found in {Context}: {Raw}",
                callerContext,
                Truncate(raw, 200)
            );
            return null;
        }

        var closeChar = cleaned[start] == '{' ? '}' : ']';
        var end = cleaned.LastIndexOf(closeChar);
        if (end <= start)
        {
            _logger.LogWarning("[OutputGuard] Unclosed JSON in {Context}", callerContext);
            return null;
        }

        var jsonStr = cleaned[start..(end + 1)];

        try
        {
            return JsonSerializer.Deserialize<T>(
                jsonStr,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "[OutputGuard] JSON parse failed in {Context}: {Json}",
                callerContext,
                Truncate(jsonStr, 200)
            );
            return null;
        }
    }

    // ── 2. Reranker output validation ────────────────────────────────

    public record RerankEntry
    {
        public int Index { get; init; }
        public double Score { get; init; }
    }

    /// <summary>
    /// Validates reranker output: must be a non-empty array of {index, score} entries.
    /// Scores must be 0.0–1.0. Indices must be within candidate range.
    /// </summary>
    public List<RerankEntry>? ValidateRerankOutput(string? raw, int candidateCount)
    {
        var entries = TryParseJson<List<RerankEntry>>(raw, "Reranker");
        if (entries is null || entries.Count == 0)
            return null;

        // Filter invalid entries
        var valid = entries
            .Where(e => e.Index >= 0 && e.Index < candidateCount)
            .Where(e => e.Score >= 0.0 && e.Score <= 1.0)
            .ToList();

        if (valid.Count == 0)
        {
            _logger.LogWarning(
                "[OutputGuard] Reranker returned {Count} entries, none valid (candidateCount={Candidates})",
                entries.Count,
                candidateCount
            );
            return null;
        }

        if (valid.Count < entries.Count)
        {
            _logger.LogInformation(
                "[OutputGuard] Reranker: {Dropped} of {Total} entries dropped (out of range)",
                entries.Count - valid.Count,
                entries.Count
            );
        }

        return valid;
    }

    // ── 3. Recipe sanity checks ──────────────────────────────────────

    /// <summary>
    /// Checks a recipe for missing/invalid data. Returns true if the recipe
    /// is usable. Logs and increments counter for sanitized recipes.
    /// </summary>
    public bool IsRecipeSane(RecipeDocument recipe, double? score = null)
    {
        if (string.IsNullOrWhiteSpace(recipe.Title))
        {
            _logger.LogWarning("[OutputGuard] Recipe with empty title dropped");
            Interlocked.Increment(ref _recipesSanitized);
            return false;
        }

        if (recipe.Ingredients is null || recipe.Ingredients.Count == 0)
        {
            _logger.LogWarning(
                "[OutputGuard] Recipe '{Title}' has no ingredients — dropped",
                recipe.Title
            );
            Interlocked.Increment(ref _recipesSanitized);
            return false;
        }

        if (score.HasValue && score.Value < 0.3)
        {
            _logger.LogInformation(
                "[OutputGuard] Recipe '{Title}' has low relevance score {Score} — dropped",
                recipe.Title,
                score.Value
            );
            Interlocked.Increment(ref _recipesSanitized);
            return false;
        }

        return true;
    }

    // ── 4. Substitution hallucination guard ──────────────────────────

    /// <summary>
    /// Verifies that a substitution suggestion exists in the known substitution map.
    /// LLM-invented substitutions that aren't in the map are flagged.
    /// </summary>
    public bool IsSubstitutionKnown(
        string matchedRule,
        string suggestion,
        Dictionary<string, List<string>> substitutionMap
    )
    {
        if (!substitutionMap.TryGetValue(matchedRule, out var knownSubs))
            return false; // no known substitutions for this rule at all

        return knownSubs.Any(s =>
            s.Contains(suggestion, StringComparison.OrdinalIgnoreCase)
            || suggestion.Contains(s, StringComparison.OrdinalIgnoreCase)
        );
    }

    // ── 5. Entity extraction validation ──────────────────────────────

    public record ExtractedProfile
    {
        public List<string> Restrictions { get; init; } = [];
        public List<string> Allergies { get; init; } = [];
        public List<string> Uncertain { get; init; } = [];
        public string Confidence { get; init; } = "low";
    }

    /// <summary>
    /// Validates LLM entity extraction output. Rejects low-confidence extractions.
    /// </summary>
    public ExtractedProfile? ValidateEntityExtraction(string? raw)
    {
        var extracted = TryParseJson<ExtractedProfile>(raw, "EntityExtraction");
        if (extracted is null)
            return null;

        if (extracted.Confidence.Equals("low", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("[OutputGuard] Entity extraction confidence=low — discarding");
            return null;
        }

        // Cap extraction size — LLM shouldn't return 20 restrictions from one sentence
        if (extracted.Restrictions.Count + extracted.Allergies.Count > 5)
        {
            _logger.LogWarning(
                "[OutputGuard] Entity extraction returned {Count} items — likely over-extraction",
                extracted.Restrictions.Count + extracted.Allergies.Count
            );
            // Still return, but this is logged for prompt tuning
        }

        return extracted;
    }

    // ── 6. LLM retry wrapper ─────────────────────────────────────────

    /// <summary>
    /// Calls an LLM function, validates the output, retries once on failure.
    /// Returns validated result or null (caller falls back to non-LLM path).
    /// </summary>
    public async Task<T?> CallWithRetryAsync<T>(
        Func<Task<string?>> llmCall,
        Func<string?, T?> validator,
        string context
    )
        where T : class
    {
        // First attempt
        var raw = await llmCall();
        var result = validator(raw);
        if (result is not null)
            return result;

        // Retry once
        _logger.LogInformation(
            "[OutputGuard] Retry in {Context} — first attempt returned invalid output",
            context
        );
        Interlocked.Increment(ref _llmRetryCount);
        _audit.Record("llm_retry", "system", context);

        raw = await llmCall();
        result = validator(raw);
        if (result is not null)
            return result;

        // Both failed — caller uses fallback
        _logger.LogWarning("[OutputGuard] Fallback in {Context} — both attempts failed", context);
        Interlocked.Increment(ref _llmFallbackCount);
        _audit.Record("llm_fallback", "system", context);
        return null;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "...";
}
