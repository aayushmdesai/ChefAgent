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
        HttpClient httpClient, // reserved for Month 2 LLM classification
        string ollamaUrl, // reserved for Month 2 LLM classification
        string chatModel, // reserved for Month 2 LLM classification
        ILogger<IntentRouter> logger
    )
    {
        _logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Classifies a user message and extracts entities.
    /// Rules-only for MVP — no LLM calls.
    /// "rules-default" in ClassifiedBy means SearchRecipe was assumed, not matched.
    /// Collect these cases for future dataset labeling.
    /// </summary>
    public Task<ClassifiedIntent> ClassifyAsync(
        string message,
        DietaryProfile? existingProfile = null,
        string? sessionId = null
    ) // ← add
    {
        var lower = message.ToLowerInvariant().Trim();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var intent = ClassifyIntent(lower);
        var extractedProfile = ExtractProfile(lower);
        var mergedProfile = MergeProfiles(existingProfile, extractedProfile);
        var classifiedBy = intent == UserIntent.SearchRecipe ? "rules-default" : "rules";

        // Extract day,slot and constraint for ModifyMealPlan
        string? targetDay = null;
        string? targetSlot = null;
        string? modifyConstraint = null;
        if (intent == UserIntent.ModifyMealPlan)
        {
            var slots = new[] { "breakfast", "lunch", "dinner" };
            targetSlot = slots.FirstOrDefault(s => lower.Contains(s)) ?? "dinner";
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
            var matched = days.FirstOrDefault(d => lower.Contains(d));
            targetDay = matched is not null
                ? char.ToUpper(matched[0]) + matched[1..] // "tuesday" → "Tuesday"
                : null;
            modifyConstraint = ExtractModifyConstraint(lower);
        }

        sw.Stop();
        _logger.LogInformation(
            "[IntentRouter] Message='{Msg}' Intent={Intent} Layer={Layer} Time={Ms}ms",
            message,
            intent,
            classifiedBy,
            sw.ElapsedMilliseconds
        );
        var mealSlots = intent == UserIntent.CreateMealPlan ? ExtractMealSlots(lower) : ["dinner"];
        return Task.FromResult(
            new ClassifiedIntent
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
            }
        );
    }

    // ── Intent Classification ─────────────────────────────────────────────────

    private static UserIntent ClassifyIntent(string lower)
    {
        if (ValidateDietSignals.Any(s => lower.Contains(s)))
            return UserIntent.ValidateDiet;

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
            restrictions.Add("nuts");
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
        return string.IsNullOrWhiteSpace(cleaned) ? lower : cleaned;
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
