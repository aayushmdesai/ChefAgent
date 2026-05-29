namespace ChefAgent.Agents.Orchestrator;

using System.Text.RegularExpressions;
using ChefAgent.Shared.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Classifies user intent and extracts entities from natural language messages.
///
/// Rules-only for MVP — LLM classification deferred until labeled dataset exists.
///
/// Intent classification:
///   ValidateDiet    : unambiguous signal phrases ("can I eat", "allergic to", ...)
///   CreateMealPlan  : explicit meal plan phrases ("plan my week", "meal plan", ...)
///   ModifyMealPlan  : modification phrases ("swap", "replace Tuesday", ...)
///   GeneralQuestion : question phrases ("what is", "how do I", "explain", ...)
///   SearchRecipe    : DEFAULT — everything else. Most common intent.
///                     Logged as "rules-default" for future dataset collection.
///
/// Entity extraction (rules-based):
///   - "allergic to X"     → allergies: ["X"]
///   - "gluten-free X"     → restrictions: ["gluten-free"]
///   - "vegan X"           → restrictions: ["vegan"]
///   - etc.
///
/// Profile merging:
///   Extracted profile (from message) + existing profile (from request DTO)
///   are merged without duplicates — no information lost.
/// </summary>
public class IntentRouter
{
    private readonly ILogger<IntentRouter> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _ollamaUrl;
    private readonly string _chatModel;
    // ── Signal Word Sets ──────────────────────────────────────────────────────

    private static readonly HashSet<string> ValidateDietSignals =
    [
        "can i eat",
        "is this safe",
        "safe for me",
        "safe for",
        "allergic to",
        "check this recipe",
        "does this contain",
        "does this have",
        "suitable for",
        "okay for me",
        "will this work for",
        "is this okay",
    ];
    // Implicit constraint signals — heuristic to detect when LLM extraction is needed.
    // Rules catch explicit vocabulary ("gluten-free", "vegan").
    // LLM catches natural language expressions of constraints.
    private static readonly HashSet<string> ImplicitConstraintSignals =
    [
        "i cannot", "i can not", "i do not eat", "i dont eat",
    "i am allergic", "i am intolerant", "i am sensitive",
    "we cannot", "we do not eat", "we dont eat",
    "no X for me", "avoid", "i need to avoid",
    "i am trying", "i try to", "i have been",
    "plant-based", "clean eating", "healthy eating",
    "my doctor", "my diet", "intolerant", "sensitive to",
    "i have a", "we have a",        // "i have a nut allergy"
    "i follow", "we follow",        // "i follow a vegan diet"
    "i eat", "i only eat",          // "i only eat fish"
    "no dairy", "no gluten", "no nuts", "no meat",
];

    private static readonly HashSet<string> GeneralQuestionSignals =
    [
        "what is ",
        "what are ",
        "what does ",
        "how do i ",
        "how do you ",
        "how to ",
        "tell me about",
        "explain ",
        "what's the difference",
        "why is ",
        "why does ",
    ];

    private static readonly HashSet<string> MealPlanSignals =
    [
        "plan my week",
        "plan my dinners for the week",
        "plan my dinners",
        "plan my lunches",
        "plan my breakfasts",
        "plan my breakfast and lunch",
        "plan my lunch and dinner",
        "plan my breakfast and dinner",
        "plan my breakfast, lunch",
        "plan my meals for the week",
        "plan my meals",
        "meal plan",
        "what should i eat this week",
        "weekly plan",
        "plan for the week",
        "schedule meals",
    ];

    private static readonly HashSet<string> ModifyMealPlanSignals =
    [
        "swap ",
        "replace tuesday",
        "replace monday",
        "replace wednesday",
        "replace thursday",
        "replace friday",
        "switch tuesday",
        "update my plan",
        "different recipe for",
    ];
    private static readonly HashSet<string> GetMealPlanSignals =
    [
        "show me my plan",
        "what is my plan",
        "my meal plan",
        "what am i eating",
        "what is for dinner this week",
        "what is for dinner tonight",
        "show my plan",
        "get my plan",
        "view my plan",
        "my plan",
        "my weekly plan",
    ];
    // ── Contraction Normalization ─────────────────────────────────────────────
    /// Expands common contractions before signal word matching.
    /// Handles the most common cases — not exhaustive.
    /// LLM classifier (Month 2) handles arbitrary phrasing.
    /// Runs once on lowercased input — all signal sets benefit automatically.

    /// <summary>
    /// Expands common contractions before signal word matching.
    /// Runs once on the lowercased input — all signal sets benefit automatically.
    /// </summary>
    private static string NormalizeContractions(string lower) => lower
        .Replace("whats", "what is")
        .Replace("what's", "what is")
        .Replace("hows", "how is")
        .Replace("how's", "how is")
        .Replace("whos", "who is")
        .Replace("who's", "who is")
        .Replace("thats", "that is")
        .Replace("that's", "that is")
        .Replace("its ", "it is ")
        .Replace("it's", "it is")
        .Replace("i'm", "i am")
        .Replace("im ", "i am ")
        .Replace("cant", "cannot")
        .Replace("can't", "cannot")
        .Replace("dont", "do not")
        .Replace("don't", "do not")
        .Replace("doesnt", "does not")
        .Replace("doesn't", "does not")
        .Replace("wont", "will not")
        .Replace("won't", "will not")
        .Replace("isnt", "is not")
        .Replace("isn't", "is not")
        .Replace("wouldnt", "would not")
        .Replace("wouldn't", "would not")
        .Replace("shouldnt", "should not")
        .Replace("shouldn't", "should not")
        .Replace("couldnt", "could not")
        .Replace("couldn't", "could not");
    private static List<string> ExtractMealSlots(string lower)
    {
        var slots = new List<string>();

        if (lower.Contains("breakfast"))
            slots.Add("breakfast");
        if (lower.Contains("lunch") || lower.Contains("lunches"))
            slots.Add("lunch");
        if (lower.Contains("dinner") || lower.Contains("dinners"))
            slots.Add("dinner");

        // "meals", "meal plan", "plan my week" with no specific slot = all three
        if (slots.Count == 0)
            slots = ["breakfast", "lunch", "dinner"];

        return slots;
    }

    public IntentRouter(
    HttpClient httpClient,
    string ollamaUrl,
    string chatModel,
    ILogger<IntentRouter> logger)
    {
        _httpClient = httpClient;
        _ollamaUrl = ollamaUrl;
        _chatModel = chatModel;
        _logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Classifies a user message and extracts entities.
    /// Rules-only for MVP — no LLM calls.
    /// "rules-default" in ClassifiedBy means SearchRecipe was assumed, not matched.
    /// Collect these cases for future dataset labeling.
    /// </summary>
    public async Task<ClassifiedIntent> ClassifyAsync(
    string message,
    DietaryProfile? existingProfile = null,
    string? sessionId = null,
    List<ConversationEntry>? history = null)
    {
        var lower = message.ToLowerInvariant().Trim();
        var normalized = NormalizeContractions(lower);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var intent = ClassifyIntent(normalized);
        var extractedProfile = ExtractProfile(normalized);

        // ── LLM entity extraction fallback ───────────────────────
        // Only fires when:
        //   1. Rules extracted nothing (extractedProfile is null)
        //   2. Message contains implicit constraint signals
        //   3. Existing profile doesn't already cover it
        if (extractedProfile is null && HasImplicitConstraintSignal(normalized))
        {
            var llmProfile = await TryExtractProfileWithLlmAsync(
                message, existingProfile, history);

            if (llmProfile is not null)
            {
                extractedProfile = llmProfile;
                _logger.LogInformation(
                    "[IntentRouter] LLM extracted profile — restrictions: [{R}] allergies: [{A}]",
                    string.Join(", ", llmProfile.Restrictions),
                    string.Join(", ", llmProfile.Allergies));
            }
        }

        var mergedProfile = MergeProfiles(existingProfile, extractedProfile);
        var classifiedBy = intent == UserIntent.SearchRecipe ? "rules-default" : "rules";

        // Extract day, slot and constraint for ModifyMealPlan
        string? targetDay = null;
        string? targetSlot = null;
        string? modifyConstraint = null;
        if (intent == UserIntent.ModifyMealPlan)
        {
            var slots = new[] { "breakfast", "lunch", "dinner" };
            targetSlot = slots.FirstOrDefault(s => lower.Contains(s)) ?? "dinner";
            var days = new[]
            {
            "monday", "tuesday", "wednesday", "thursday",
            "friday", "saturday", "sunday",
        };
            var matched = days.FirstOrDefault(d => lower.Contains(d));
            targetDay = matched is not null
                ? char.ToUpper(matched[0]) + matched[1..]
                : null;
            modifyConstraint = ExtractModifyConstraint(lower);
        }

        sw.Stop();
        _logger.LogInformation(
            "[IntentRouter] Message='{Msg}' Intent={Intent} Layer={Layer} Time={Ms}ms",
            message, intent, classifiedBy, sw.ElapsedMilliseconds);

        var mealSlots = intent == UserIntent.CreateMealPlan
            ? ExtractMealSlots(lower)
            : ["dinner"];

        return new ClassifiedIntent
        {
            Intent = intent,
            SearchQuery = ExtractSearchQuery(lower, intent),
            OriginalMessage = message,
            ExtractedProfile = extractedProfile,
            MergedProfile = mergedProfile,
            SessionId = sessionId,
            TargetDay = targetDay,
            TargetSlot = targetSlot ?? "dinner",
            MealSlots = mealSlots,
            ModifyConstraint = modifyConstraint,
            DeferredIntents = [],
            DeferredMessage = null,
            ClassifiedBy = classifiedBy,
        };
    }
    // ── Intent Classification ─────────────────────────────────────────────────

    private static UserIntent ClassifyIntent(string lower)
    {
        if (ValidateDietSignals.Any(s => lower.Contains(s)))
            return UserIntent.ValidateDiet;

        if (GetMealPlanSignals.Any(s => lower.Contains(s)))
            return UserIntent.GetMealPlan;

        if (MealPlanSignals.Any(s => lower.Contains(s)))
            return UserIntent.CreateMealPlan;

        if (ModifyMealPlanSignals.Any(s => lower.Contains(s)))
            return UserIntent.ModifyMealPlan;

        if (GeneralQuestionSignals.Any(s => lower.Contains(s)))
            return UserIntent.GeneralQuestion;

        // Default — SearchRecipe is the most common intent.
        // Natural language for recipe search is too varied for keyword rules.
        // Log as "rules-default" — collect for future LLM classifier training.
        return UserIntent.SearchRecipe;
    }

    // ── Entity Extraction ─────────────────────────────────────────────────────

    private static DietaryProfile? ExtractProfile(string lower)
    {
        var allergies = new List<string>();
        var restrictions = new List<string>();

        // "allergic to X" — extract the allergen
        var allergyMatch = Regex.Match(lower, @"allergic to (\w+)");
        if (allergyMatch.Success)
            allergies.Add(allergyMatch.Groups[1].Value);

        // Explicit restriction terms embedded in the query
        if (lower.Contains("gluten-free") || lower.Contains("gluten free"))
            restrictions.Add("gluten-free");
        if (lower.Contains("dairy-free") || lower.Contains("dairy free"))
            restrictions.Add("dairy-free");
        if (lower.Contains("nut-free") || lower.Contains("nut free"))
            restrictions.Add("nut-free");
        if (lower.Contains("vegan"))
            restrictions.Add("vegan");
        if (lower.Contains("vegetarian"))
            restrictions.Add("vegetarian");
        if (lower.Contains("pescatarian"))
            restrictions.Add("pescatarian");
        if (lower.Contains("jain"))
            restrictions.Add("jain");
        if (lower.Contains("sattvic"))
            restrictions.Add("sattvic");
        if (lower.Contains("halal"))
            restrictions.Add("halal");
        if (lower.Contains("kosher"))
            restrictions.Add("kosher");

        if (allergies.Count == 0 && restrictions.Count == 0)
            return null;

        return new DietaryProfile { Allergies = allergies, Restrictions = restrictions };
    }

    private static string ExtractSearchQuery(string lower, UserIntent intent)
    {
        // Only clean queries for SearchRecipe — other intents use the full message
        if (intent != UserIntent.SearchRecipe)
            return lower;

        var cleaned = lower
            // Action words
            .Replace("i cannot have ", "")
            .Replace("i can not have ", "")
            .Replace("i do not eat ", "")
            .Replace("i dont eat ", "")
            .Replace("i am allergic to ", "")
            .Replace("i am intolerant to ", "")
            .Replace("no dairy ", "")
            .Replace("no gluten ", "")
            .Replace("no nuts ", "")
            .Replace("no meat ", "")
            .Replace(",", "")
            .Replace("find me ", "")
            .Replace("find ", "")
            .Replace("search for ", "")
            .Replace("show me ", "")
            .Replace("i want ", "")
            .Replace("i need ", "")
            .Replace("suggest ", "")
            .Replace("give me ", "")
            .Replace("looking for ", "")
            .Replace("what can i make with ", "")
            .Replace("recipe for ", "")
            .Replace("recipes for ", "")
            .Replace("ideas for ", "")
            // Dietary prefixes — already extracted into profile
            .Replace("gluten-free ", "")
            .Replace("gluten free ", "")
            .Replace("dairy-free ", "")
            .Replace("dairy free ", "")
            .Replace("nut-free ", "")
            .Replace("nut free ", "")
            .Replace("vegan ", "")
            .Replace("vegetarian ", "")
            .Replace("pescatarian ", "")
            .Replace("jain ", "")
            .Replace("sattvic ", "")
            .Replace("halal ", "")
            .Replace("kosher ", "")
            // Trailing filler
            .Replace(" dinner", " ")
            .Replace(" lunch", " ")
            .Replace(" breakfast", " ")
            .Replace(" recipes", " ")
            .Replace(" recipe", " ")
            .Replace(" ideas", " ")
            .Replace(" tonight", " ")
            .Replace(" dinners", " ")
            .Replace(" dinner", " ")
            .Replace(" lunches", " ")
            .Replace(" lunch", " ")
            .Replace(" breakfasts", " ")
            .Replace(" breakfast", " ")
            .Trim();

        // Fall back to original if cleaning removed everything
        return string.IsNullOrWhiteSpace(cleaned) ? "dinner" : cleaned;
    }
    // ── LLM Entity Extraction ─────────────────────────────────────────────────

    private static bool HasImplicitConstraintSignal(string normalized) =>
        ImplicitConstraintSignals.Any(s => normalized.Contains(s));

    /// <summary>
    /// Calls Ollama to extract dietary constraints from natural language.
    /// Passes conversation history + existing profile so the LLM has full context
    /// and doesn't re-extract already-known constraints.
    /// Returns null on timeout or parse failure — graceful degradation.
    /// </summary>
    private async Task<DietaryProfile?> TryExtractProfileWithLlmAsync(
        string message,
        DietaryProfile? existingProfile,
        List<ConversationEntry>? history)
    {
        try
        {
            var historyText = history is not null && history.Count > 0
                ? string.Join("\n", history.TakeLast(6).Select(e =>
                    $"{e.Role}: {e.Content}"))
                : "No prior conversation.";

            var knownProfile = existingProfile is null
                ? "None"
                : $"restrictions: [{string.Join(", ", existingProfile.Restrictions)}], " +
                  $"allergies: [{string.Join(", ", existingProfile.Allergies)}]";

            var outputFormat = """{"restrictions": ["vegetarian"], "allergies": ["nuts"], "uncertain": ["healthy"], "confidence": "high"}""";
var emptyFormat  = """{"restrictions": [], "allergies": [], "uncertain": [], "confidence": "high"}""";

var prompt =
    $"You are a dietary constraint extractor. Extract ONLY NEW dietary " +
    $"restrictions or allergies from the conversation below that are NOT " +
    $"already in the known profile.\n\n" +
    $"Known profile (do NOT re-extract these): {knownProfile}\n\n" +
    $"Conversation history:\n{historyText}\n\n" +
    $"Current message: {message}\n\n" +
    $"Return ONLY valid JSON, no explanation, no markdown fences:\n" +
    $"{outputFormat}\n\n" +
    $"Use these exact restriction names where applicable:\n" +
    $"vegetarian, vegan, pescatarian, jain, sattvic, halal, kosher, " +
    $"gluten-free, dairy-free, nut-free, egg-free, soy-free\n\n" +
    $"If nothing new was expressed, return:\n{emptyFormat}";
            var payload = new
            {
                model = _chatModel,
                stream = false,
                messages = new[]
                {
                new { role = "user", content = prompt },
            },
            };

            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            var content = new System.Net.Http.StringContent(
                json, System.Text.Encoding.UTF8, "application/json");

            // 90s timeout — LLM extraction is opt-in, caller already opted in
            using var cts = new System.Threading.CancellationTokenSource(
                TimeSpan.FromSeconds(90));
            var response = await _httpClient.PostAsync(
                $"{_ollamaUrl}/api/chat", content, cts.Token);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var raw = doc.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;

            _logger.LogInformation("[IntentRouter] LLM raw response: {Raw}", raw);
            // Strip markdown fences if present
            raw = raw.Trim();
            if (raw.StartsWith("```")) raw = raw[(raw.IndexOf('\n') + 1)..];
            if (raw.EndsWith("```"))   raw = raw[..raw.LastIndexOf("```")].Trim();

            // Extract first JSON object only — LLM sometimes appends explanation after
            var braceStart = raw.IndexOf('{');
            var braceEnd   = raw.LastIndexOf('}');
            if (braceStart < 0 || braceEnd < 0 || braceEnd <= braceStart)
            {
                _logger.LogWarning("[IntentRouter] LLM response had no valid JSON object");
                return null;
            }
            raw = raw[braceStart..(braceEnd + 1)];

            using var result = System.Text.Json.JsonDocument.Parse(raw);
            var root = result.RootElement;

            var confidence = root.TryGetProperty("confidence", out var c)
                ? c.GetString() : "low";

            // Low confidence → don't apply, log and return null
            if (confidence == "low")
            {
                _logger.LogInformation(
                    "[IntentRouter] LLM extraction confidence=low — not applying");
                return null;
            }

            var restrictions = root.TryGetProperty("restrictions", out var r)
                ? r.EnumerateArray().Select(x => x.GetString()!).Where(s => !string.IsNullOrEmpty(s)).ToList()
                : new List<string>();

            var allergies = root.TryGetProperty("allergies", out var a)
                ? a.EnumerateArray().Select(x => x.GetString()!).Where(s => !string.IsNullOrEmpty(s)).ToList()
                : new List<string>();

            if (restrictions.Count == 0 && allergies.Count == 0)
                return null;

            return new DietaryProfile
            {
                Restrictions = restrictions,
                Allergies = allergies,
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[IntentRouter] LLM entity extraction timed out — proceeding without");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IntentRouter] LLM entity extraction failed — proceeding without");
            return null;
        }
    }

    // ── Profile Merging ───────────────────────────────────────────────────────

    private static string? ExtractModifyConstraint(string lower)
    {
        // "swap tuesday to something with pasta" → "pasta"
        // "change wednesday to italian"         → "italian"
        // "swap friday to something simpler"    → "simpler"
        var patterns = new[] { "to something with ", "to something ", "to ", "with " };
        foreach (var pattern in patterns)
        {
            var idx = lower.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var after = lower[(idx + pattern.Length)..].Trim();
                // Strip day names that leaked into the constraint
                var days = new[]
                {
                    "monday",
                    "tuesday",
                    "wednesday",
                    "thursday",
                    "friday",
                    "saturday",
                    "sunday",
                };
                foreach (var d in days)
                    after = after.Replace(d, "").Trim();
                if (!string.IsNullOrWhiteSpace(after))
                    return after;
            }
        }
        return null;
    }

    private static DietaryProfile? MergeProfiles(
        DietaryProfile? existing,
        DietaryProfile? extracted
    )
    {
        if (existing is null && extracted is null)
            return null;
        if (existing is null)
            return extracted;
        if (extracted is null)
            return existing;

        return new DietaryProfile
        {
            Restrictions = existing
                .Restrictions.Union(extracted.Restrictions, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Allergies = existing
                .Allergies.Union(extracted.Allergies, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }
}

/// <summary>
/// Result of intent classification — intent + all extracted entities.
/// ClassifiedBy values: "rules" | "rules-default" | "llm" (Month 2)
/// "rules-default" means SearchRecipe was assumed, not explicitly matched.
/// Collect these cases as training data for future LLM classifier.
/// </summary>
public record ClassifiedIntent
{
    public required UserIntent Intent { get; init; }
    public required string SearchQuery { get; init; }
    public string OriginalMessage { get; init; } = string.Empty;
    public DietaryProfile? ExtractedProfile { get; init; }
    public DietaryProfile? MergedProfile { get; init; }
    public string? SessionId { get; set; }
    public string? TargetDay { get; set; }
    public string TargetSlot { get; set; } = "dinner";
    public string? ModifyConstraint { get; set; }
    public List<string> MealSlots { get; init; } = ["dinner"];
    public List<UserIntent> DeferredIntents { get; init; } = [];
    public string? DeferredMessage { get; init; }
    public required string ClassifiedBy { get; init; }
}
