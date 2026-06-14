namespace ChefAgent.Agents.Diet;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChefAgent.Shared;
using ChefAgent.Shared.Guardrails;
using ChefAgent.Shared.Models;
using ChefAgent.Shared.Observability;
using ChefAgent.Shared.Providers.Llm;
using Microsoft.Extensions.Logging;

/// <summary>
/// Diet Agent — validates recipes against a user's dietary profile.
///
/// Two-layer design:
///   Layer 1 — DietaryRules  : static knowledge base, instant, zero LLM calls.
///                              Handles the common 80% of cases.
///   Layer 2 — LLM           : called only when rules are insufficient.
///                              Handles edge cases, ambiguous ingredients,
///                              and unknown restriction types.
///
/// LLM escalation conditions:
///   - Profile contains a restriction the rules engine doesn't know about
///   - Rules found nothing but query explicitly requests deep validation
///   - Ingredient list contains ambiguous items (e.g. "natural flavors", "spice blend")
///
/// Substitution suggestions:
///   - Known violations → rules-based substitution knowledge base (instant)
///   - Complex/ambiguous cases → LLM suggests context-aware substitutions
/// </summary>
public class DietValidationPlugin
{
    private readonly ILlmProvider _llmProvider;
    private readonly CircuitBreaker _circuitBreaker;
    private readonly ILogger<DietValidationPlugin> _logger;
    private readonly Tracing _tracing;

    // ── Substitution Knowledge Base ───────────────────────────────────────────
    // Maps a known violating ingredient phrase to its substitution options.
    // Key  : lowercase ingredient phrase that matched a rule
    // Value: list of substitutions (first = most common recommendation)

    private static readonly Dictionary<string, List<string>> SubstitutionMap = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        // Dairy substitutions
        ["butter"] = ["vegan butter", "coconut oil", "olive oil"],
        ["ghee"] = ["vegan butter", "coconut oil"],
        ["milk"] = ["oat milk", "almond milk", "soy milk", "coconut milk"],
        ["whole milk"] = ["oat milk", "full-fat coconut milk"],
        ["buttermilk"] = ["oat milk + 1 tbsp apple cider vinegar", "soy milk + 1 tbsp lemon juice"],
        ["heavy cream"] = ["coconut cream", "cashew cream", "oat cream"],
        ["cream"] = ["coconut cream", "cashew cream"],
        ["sour cream"] = ["coconut yogurt", "cashew sour cream"],
        ["cheese"] = ["nutritional yeast", "cashew cheese", "vegan cheese"],
        ["cheddar"] = ["vegan cheddar", "nutritional yeast"],
        ["mozzarella"] = ["vegan mozzarella", "cashew mozzarella"],
        ["parmesan"] = ["nutritional yeast", "vegan parmesan"],
        ["cream cheese"] = ["cashew cream cheese", "vegan cream cheese"],
        ["yogurt"] = ["coconut yogurt", "soy yogurt", "oat yogurt"],
        ["greek yogurt"] = ["coconut greek yogurt", "cashew yogurt"],
        ["ice cream"] = ["coconut milk ice cream", "oat milk ice cream"],

        // Egg substitutions — role matters; LLM will refine for complex cases
        ["egg"] =
        [
            "flax egg (1 tbsp ground flax + 3 tbsp water)",
            "chia egg",
            "applesauce (¼ cup per egg)",
        ],
        ["eggs"] = ["flax eggs", "chia eggs", "aquafaba (3 tbsp per egg)"],
        ["egg white"] = ["aquafaba", "chickpea brine"],
        ["egg whites"] = ["aquafaba"],
        ["mayonnaise"] = ["vegan mayonnaise", "mashed avocado"],

        // Gluten substitutions
        ["all-purpose flour"] =
        [
            "gluten-free all-purpose flour",
            "almond flour",
            "oat flour (if oat-tolerant)",
        ],
        ["wheat flour"] = ["gluten-free flour blend", "rice flour"],
        ["bread crumbs"] = ["gluten-free breadcrumbs", "crushed rice crackers", "almond meal"],
        ["breadcrumbs"] = ["gluten-free breadcrumbs", "crushed rice crackers"],
        ["panko"] = ["gluten-free panko", "crushed gluten-free crackers"],
        ["soy sauce"] = ["tamari (gluten-free)", "coconut aminos"],
        ["pasta"] = ["gluten-free pasta", "rice noodles", "zucchini noodles"],
        ["noodles"] = ["rice noodles", "gluten-free noodles"],
        ["bread"] = ["gluten-free bread"],
        ["flour tortilla"] = ["corn tortilla", "gluten-free tortilla"],
        ["beer"] = ["gluten-free beer", "apple cider (in cooking)"],
        ["hoisin sauce"] = ["gluten-free hoisin sauce"],
        ["teriyaki sauce"] = ["gluten-free teriyaki sauce"],

        // Meat substitutions
        ["chicken"] = ["tofu", "tempeh", "seitan (if no gluten restriction)", "jackfruit"],
        ["beef"] = ["lentils", "black beans", "mushrooms", "plant-based ground meat"],
        ["pork"] = ["tempeh", "jackfruit", "plant-based sausage"],
        ["bacon"] = ["coconut bacon", "tempeh bacon", "smoked paprika + mushrooms"],
        ["ham"] = ["smoked tofu", "plant-based ham"],
        ["sausage"] = ["plant-based sausage", "lentil sausage"],
        ["ground beef"] = ["plant-based ground meat", "lentils", "walnut meat"],
        ["lamb"] = ["jackfruit", "lentils", "plant-based ground meat"],
        ["gelatin"] = ["agar-agar", "pectin", "carrageenan"],
        ["lard"] = ["coconut oil", "vegetable shortening"],

        // Seafood substitutions
        ["fish"] = ["tofu", "hearts of palm", "jackfruit"],
        ["shrimp"] = ["king oyster mushrooms", "hearts of palm"],
        ["tuna"] = ["chickpeas (smashed)", "hearts of palm"],
        ["salmon"] = ["carrot lox", "smoked tofu"],
        ["anchovies"] = ["capers + a dash of seaweed", "miso paste"],
        ["fish sauce"] = ["soy sauce + seaweed", "coconut aminos + seaweed"],

        // Jain / Sattvic substitutions (onion, garlic)
        ["onion"] = ["asafoetida (hing) — use ¼ tsp in hot oil", "fennel bulb (for texture)"],
        ["onions"] = ["asafoetida (hing)", "fennel bulb"],
        ["garlic"] =
        [
            "asafoetida (hing) — use ¼ tsp in hot oil",
            "garlic-infused oil (if FODMAP only)",
        ],
        ["garlic powder"] = ["asafoetida (hing)"],

        // Nut substitutions
        ["almond flour"] = ["sunflower seed flour", "oat flour"],
        ["cashew cream"] = ["sunflower seed cream", "coconut cream"],
        ["peanut butter"] =
        [
            "sunflower seed butter",
            "almond butter (if only peanut allergy)",
            "tahini",
        ],

        // Honey (vegan)
        ["honey"] = ["maple syrup", "agave nectar", "date syrup"],

        // Paleo
        ["rice"] = ["cauliflower rice"],
        ["pasta"] = ["zucchini noodles", "spaghetti squash"],
        ["bread"] = ["lettuce wraps", "sweet potato slices"],
    };

    // ── Ambiguous Ingredient Signals ──────────────────────────────────────────
    // These phrases in an ingredient list suggest the rules engine can't be sure
    // — escalate to LLM for deep validation.
    private static readonly HashSet<string> AmbiguousSignals =
    [
        "natural flavors",
        "natural flavor",
        "spice blend",
        "spices",
        "seasoning",
        "seasoning mix",
        "mixed seasoning",
        "sauce",
        "gravy",
        "stock",
        "broth", // composition unknown
        "dressing",
        "marinade",
        "paste",
        "may contain",
    ];
    private static readonly HashSet<string> KnownSafePhrases =
    [
        "worcestershire sauce",
        "soy sauce",
        "fish sauce",
        "hot sauce",
        "tomato sauce",
        "pasta sauce",
        "vegetable broth",
        "chicken broth",
        "beef broth",
        "vegetable stock",
    ];

    public DietValidationPlugin(
        ILlmProvider llmProvider,
        CircuitBreaker circuitBreaker,
        Tracing tracing,
        ILogger<DietValidationPlugin> logger
    )
    {
        _llmProvider = llmProvider;
        _circuitBreaker = circuitBreaker;
        _tracing = tracing;
        _logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Main entry point. Validates a recipe against a dietary profile.
    ///
    /// Returns a DietaryValidation with:
    ///   - IsCompatible   : true if no violations found by either layer
    ///   - Violations     : list of ViolationDetails (with DetectedBy = Rules or Llm)
    ///   - Substitutions  : suggestions for each violation
    ///   - Explanation    : human-readable summary (LLM-generated if LLM was called)
    /// </summary>
    public async Task<DietaryValidation> ValidateRecipeAsync(
        RecipeDocument recipe,
        DietaryProfile? profile,
        TraceContext? parentCtx = null
    )
    {
        using (_logger.BeginScope(new { CorrelationId = parentCtx?.CorrelationId ?? "none" }))
        {
            // No profile — skip all validation
            if (
                profile is null
                || (profile.Allergies.Count == 0 && profile.Restrictions.Count == 0)
            )
            {
                _logger.LogDebug(
                    "No dietary profile — skipping validation for recipe {Id}",
                    recipe.Id
                );
                return Compatible(recipe.Id, "No dietary restrictions provided.");
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();

            _logger.LogInformation(
                "Validating recipe '{Title}' | allergies: [{Allergies}] restrictions: [{Restrictions}]",
                recipe.Title,
                string.Join(", ", profile.Allergies),
                string.Join(", ", profile.Restrictions)
            );

            // ── Layer 1: Rules Engine ─────────────────────────────────────────────
            var rulesViolations = DietaryRules.Validate(recipe.Ingredients, profile);

            if (rulesViolations.Count > 0)
            {
                sw.Stop();
                _logger.LogInformation(
                    "[DietAgent] Recipe='{Title}' Layer=Rules Time={Ms}ms Violations={Count} Compatible=false",
                    recipe.Title,
                    sw.ElapsedMilliseconds,
                    rulesViolations.Count
                );

                var substitutions = SuggestSubstitutionsFromRules(rulesViolations);
                return new DietaryValidation
                {
                    RecipeId = recipe.Id,
                    IsCompatible = false,
                    Violations = rulesViolations,
                    Substitutions = substitutions,
                    Explanation = BuildRulesExplanation(rulesViolations),
                };
            }

            // ── Layer 2: LLM Escalation ───────────────────────────────────────────
            var unknownRestrictions = profile
                .Restrictions.Where(r => !DietaryRules.IsKnownRestriction(r))
                .ToList();

            var unknownAllergies = profile
                .Allergies.Where(a => !DietaryRules.IsKnownAllergy(a))
                .ToList();

            var ruleMatchedIngredients = rulesViolations.Select(v => v.Ingredient).ToHashSet();

            var hasAmbiguousIngredients = recipe
                .Ingredients.Where(i =>
                    !KnownSafePhrases.Any(known =>
                        i.Contains(known, StringComparison.OrdinalIgnoreCase)
                    )
                )
                .Any(i =>
                    AmbiguousSignals.Any(signal =>
                        i.Contains(signal, StringComparison.OrdinalIgnoreCase)
                    )
                );

            // Allergy + ambiguous → LLM (could be life-threatening)
            if (profile.Allergies.Count > 0 && hasAmbiguousIngredients)
            {
                _logger.LogInformation(
                    "Escalating '{Title}' to LLM — allergy profile + ambiguous ingredients detected",
                    recipe.Title
                );
                var result = await ValidateWithLlmAsync(
                    recipe,
                    profile,
                    unknownRestrictions,
                    unknownAllergies,
                    parentCtx
                );
                sw.Stop();
                _logger.LogInformation(
                    "[DietAgent] Recipe='{Title}' Layer=Llm Time={Ms}ms Violations={Count} Compatible={Compatible}",
                    recipe.Title,
                    sw.ElapsedMilliseconds,
                    result.Violations.Count,
                    result.IsCompatible
                );
                return result;
            }

            // Restriction + ambiguous → skip recipe
            if (
                profile.Restrictions.Count > 0
                && recipe.Ingredients.Any(i =>
                    AmbiguousSignals.Any(signal =>
                        i.Contains(signal, StringComparison.OrdinalIgnoreCase)
                    )
                )
            )
            {
                sw.Stop();
                _logger.LogInformation(
                    "[DietAgent] Recipe='{Title}' Layer=Skip Time={Ms}ms Violations=0 Compatible=false — ambiguous ingredients",
                    recipe.Title,
                    sw.ElapsedMilliseconds
                );
                return new DietaryValidation
                {
                    RecipeId = recipe.Id,
                    IsCompatible = false,
                    Violations = [],
                    Substitutions = [],
                    Explanation =
                        "Recipe contains ambiguous ingredients (e.g. 'spice blend', 'natural flavors') "
                        + "that could not be verified against your dietary restrictions. Recipe skipped.",
                };
            }

            // All known, no ambiguity → rules result is final
            var needsLlm = unknownRestrictions.Count > 0 || unknownAllergies.Count > 0;

            if (!needsLlm)
            {
                sw.Stop();
                _logger.LogInformation(
                    "[DietAgent] Recipe='{Title}' Layer=Rules Time={Ms}ms Violations=0 Compatible=true",
                    recipe.Title,
                    sw.ElapsedMilliseconds
                );
                return Compatible(
                    recipe.Id,
                    $"Recipe passed all rule checks for: {string.Join(", ", profile.Restrictions.Concat(profile.Allergies))}."
                );
            }

            // Unknown restriction or allergy → LLM
            _logger.LogInformation(
                "Escalating '{Title}' to LLM | unknown: [{U}]",
                recipe.Title,
                string.Join(", ", unknownRestrictions.Concat(unknownAllergies))
            );

            var llmResult = await ValidateWithLlmAsync(
                recipe,
                profile,
                unknownRestrictions,
                unknownAllergies,
                parentCtx
            );
            sw.Stop();
            _logger.LogInformation(
                "[DietAgent] Recipe='{Title}' Layer=Llm Time={Ms}ms Violations={Count} Compatible={Compatible}",
                recipe.Title,
                sw.ElapsedMilliseconds,
                llmResult.Violations.Count,
                llmResult.IsCompatible
            );
            return llmResult;
        }
    }

    /// <summary>
    /// Suggests substitutions for a list of violations.
    /// Rules-based for known violations, LLM for complex cases.
    /// Can be called independently (e.g. user asks "how do I make this vegan?")
    /// </summary>
    public async Task<List<SubstitutionSuggestion>> SuggestSubstitutionsAsync(
        RecipeDocument recipe,
        List<ViolationDetail> violations
    )
    {
        var suggestions = SuggestSubstitutionsFromRules(violations);

        // Violations with no rules-based suggestion — ask the LLM
        var unhandled = violations
            .Where(v => !suggestions.Any(s => s.OriginalIngredient == v.Ingredient))
            .ToList();

        if (unhandled.Count > 0)
        {
            _logger.LogInformation(
                "Asking LLM for substitutions for {Count} unhandled violation(s)",
                unhandled.Count
            );
            var llmSuggestions = await SuggestSubstitutionsWithLlmAsync(recipe, unhandled);
            suggestions.AddRange(llmSuggestions);
        }

        return suggestions;
    }

    // ── Rules-Based Substitution ──────────────────────────────────────────────

    private static List<SubstitutionSuggestion> SuggestSubstitutionsFromRules(
        List<ViolationDetail> violations
    )
    {
        var suggestions = new List<SubstitutionSuggestion>();

        foreach (var violation in violations)
        {
            // Try matched rule first (most specific), then full ingredient string
            var key = violation.MatchedRule ?? violation.Ingredient;
            if (!SubstitutionMap.TryGetValue(key, out var options))
                continue;

            suggestions.Add(
                new SubstitutionSuggestion
                {
                    OriginalIngredient = violation.Ingredient,
                    SuggestedReplacement = options[0], // primary recommendation
                    Reason =
                        options.Count > 1
                            ? $"Alternatives: {string.Join(", ", options.Skip(1))}"
                            : null,
                }
            );
        }

        return suggestions;
    }

    // ── LLM Validation ────────────────────────────────────────────────────────

    private async Task<DietaryValidation> ValidateWithLlmAsync(
        RecipeDocument recipe,
        DietaryProfile profile,
        List<string> unknownRestrictions,
        List<string> unknownAllergies,
        TraceContext? parentCtx = null
    )
    {
        using (_logger.BeginScope(new { CorrelationId = parentCtx?.CorrelationId ?? "none" }))
        {
            var prompt = BuildValidationPrompt(
                recipe,
                profile,
                unknownRestrictions,
                unknownAllergies
            );

            var spanCtx = TraceContext.None;
            try
            {
                try
                {
                    spanCtx = _tracing.StartSpan(
                        parentCtx ?? TraceContext.None,
                        "diet.llm_validation",
                        input: new { recipeTitle = recipe.Title }
                    );
                }
                catch { }

                if (!_circuitBreaker.IsAllowed())
                {
                    _logger.LogInformation(
                        "[DietAgent] Circuit open — skipping LLM validation for '{Title}'",
                        recipe.Title
                    );
                    _tracing.EndSpan(spanCtx, statusMessage: "circuit_open");
                    return new DietaryValidation
                    {
                        RecipeId = recipe.Id,
                        IsCompatible = true,
                        Explanation =
                            "Rules found no violations. Deep validation skipped (LLM unavailable). Please review manually.",
                        // same shape as the existing catch block fallback
                    };
                }
                var raw = await CallLlmAsync(prompt);
                _circuitBreaker.RecordSuccess();

                _tracing.EndSpan(spanCtx, output: new { latency = "ok" });

                return ParseLlmValidation(recipe.Id, raw);
            }
            catch (Exception ex)
            {
                // Graceful fallback — LLM failure should not break the pipeline
                _logger.LogWarning(
                    ex,
                    "LLM validation failed for recipe '{Title}' — returning compatible with warning",
                    recipe.Title
                );

                _circuitBreaker.RecordFailure();

                try
                {
                    _tracing.EndSpan(spanCtx, statusMessage: "error");
                }
                catch { }

                return new DietaryValidation
                {
                    RecipeId = recipe.Id,
                    IsCompatible = true,
                    Violations = [],
                    Substitutions = [],
                    Explanation =
                        "Rules engine found no violations. Deep validation unavailable (LLM timeout). "
                        + "Please review manually for: "
                        + string.Join(", ", unknownRestrictions.Concat(unknownAllergies))
                        + ".",
                };
            }
        }
    }

    private static string BuildValidationPrompt(
        RecipeDocument recipe,
        DietaryProfile profile,
        List<string> unknownRestrictions,
        List<string> unknownAllergies
    )
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "You are a dietary restriction expert. Analyze this recipe against the user's dietary profile."
        );
        sb.AppendLine("Focus especially on the restrictions the automated rules could not verify.");
        sb.AppendLine();
        sb.AppendLine($"Recipe: {recipe.Title}");
        sb.AppendLine("Ingredients:");
        foreach (var ing in recipe.Ingredients)
            sb.AppendLine($"  - {ing}");
        sb.AppendLine();
        sb.AppendLine("User profile:");
        if (profile.Allergies.Count > 0)
            sb.AppendLine($"  Allergies: {string.Join(", ", profile.Allergies)}");
        if (profile.Restrictions.Count > 0)
            sb.AppendLine($"  Restrictions: {string.Join(", ", profile.Restrictions)}");
        sb.AppendLine();
        if (unknownRestrictions.Count > 0 || unknownAllergies.Count > 0)
        {
            sb.AppendLine("Focus on these (automated rules could not verify them):");
            foreach (var r in unknownRestrictions.Concat(unknownAllergies))
                sb.AppendLine($"  - {r}");
        }
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY a JSON object, no markdown, no explanation outside JSON:");
        sb.AppendLine(
            """
            {
              "compatible": true or false,
              "violations": [
                { "ingredient": "full ingredient string", "category": "category name", "reason": "why it violates" }
              ],
              "substitutions": [
                { "original": "ingredient", "replacement": "suggested substitute", "reason": "why this works" }
              ],
              "explanation": "one sentence summary"
            }
            """
        );

        return sb.ToString();
    }

    private DietaryValidation ParseLlmValidation(string recipeId, string raw)
    {
        try
        {
            // Strip markdown fences if present
            var json = raw.Replace("```json", "").Replace("```", "").Trim();

            var parsed = JsonSerializer.Deserialize<LlmValidationResponse>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            if (parsed is null)
                throw new JsonException("Null deserialization result");

            var violations = (parsed.Violations ?? [])
                .Select(v => new ViolationDetail
                {
                    Ingredient = v.Ingredient ?? "unknown",
                    Category = v.Category ?? "unknown",
                    DetectedBy = ValidationLayer.Llm,
                    MatchedRule = null, // LLM doesn't match rules
                })
                .ToList();

            var substitutions = (parsed.Substitutions ?? [])
                .Select(s => new SubstitutionSuggestion
                {
                    OriginalIngredient = s.Original ?? "unknown",
                    SuggestedReplacement = s.Replacement ?? "unknown",
                    Reason = s.Reason,
                })
                .ToList();

            _logger.LogInformation(
                "LLM validation complete — compatible: {C}, violations: {V}",
                parsed.Compatible,
                violations.Count
            );

            return new DietaryValidation
            {
                RecipeId = recipeId,
                IsCompatible = parsed.Compatible,
                Violations = violations,
                Substitutions = substitutions,
                Explanation = parsed.Explanation,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse LLM validation response: {Raw}", raw);
            throw;
        }
    }

    private async Task<List<SubstitutionSuggestion>> SuggestSubstitutionsWithLlmAsync(
        RecipeDocument recipe,
        List<ViolationDetail> violations
    )
    {
        var violationList = string.Join(
            "\n",
            violations.Select(v => $"  - {v.Ingredient} (category: {v.Category})")
        );

        var jsonTemplate = """[ { "original": "...", "replacement": "...", "reason": "..." } ]""";
        var prompt = $"""
            You are a culinary expert. Suggest substitutions for these ingredients in the recipe "{recipe.Title}".
            Consider the recipe context when suggesting substitutions.

            Ingredients needing substitution:
            {violationList}

            Respond with ONLY a JSON array, no markdown, no explanation:
            {jsonTemplate}
            """;

        try
        {
            if (!_circuitBreaker.IsAllowed())
            {
                _logger.LogInformation(
                    "[DietAgent] Circuit open — skipping LLM substitutions for '{Title}'",
                    recipe.Title
                );
                return [];
            }
            var raw = await CallLlmAsync(prompt);
            _circuitBreaker.RecordSuccess();
            var json = raw.Replace("```json", "").Replace("```", "").Trim();
            var items =
                JsonSerializer.Deserialize<List<LlmSubstitutionItem>>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                ) ?? [];

            return items
                .Select(i => new SubstitutionSuggestion
                {
                    OriginalIngredient = i.Original ?? "unknown",
                    SuggestedReplacement = i.Replacement ?? "unknown",
                    Reason = i.Reason,
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _circuitBreaker.RecordFailure();
            _logger.LogWarning(ex, "LLM substitution suggestion failed — returning empty list");
            return [];
        }
    }

    // ── LLM HTTP ───────────────────────────────────────────────────────────

    private async Task<string> CallLlmAsync(string prompt, CancellationToken ct = default)
    {
        var messages = new[] { new ChatMessage("user", prompt) };
        return await _llmProvider.ChatAsync(messages, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DietaryValidation Compatible(string recipeId, string explanation) =>
        new()
        {
            RecipeId = recipeId,
            IsCompatible = true,
            Violations = [],
            Substitutions = [],
            Explanation = explanation,
        };

    private static string BuildRulesExplanation(List<ViolationDetail> violations)
    {
        var grouped = violations
            .GroupBy(v => v.Category)
            .Select(g =>
                $"{g.Key} ({string.Join(", ", g.Select(v => v.MatchedRule ?? v.Ingredient))})"
            );
        return $"Recipe contains: {string.Join("; ", grouped)}.";
    }

    // ── LLM Response DTOs (internal only) ────────────────────────────────────

    private record LlmValidationResponse
    {
        [JsonPropertyName("compatible")]
        public bool Compatible { get; init; }

        [JsonPropertyName("violations")]
        public List<LlmViolationItem>? Violations { get; init; }

        [JsonPropertyName("substitutions")]
        public List<LlmSubstitutionItem>? Substitutions { get; init; }

        [JsonPropertyName("explanation")]
        public string? Explanation { get; init; }
    }

    private record LlmViolationItem
    {
        [JsonPropertyName("ingredient")]
        public string? Ingredient { get; init; }

        [JsonPropertyName("category")]
        public string? Category { get; init; }

        [JsonPropertyName("reason")]
        public string? Reason { get; init; }
    }

    private record LlmSubstitutionItem
    {
        [JsonPropertyName("original")]
        public string? Original { get; init; }

        [JsonPropertyName("replacement")]
        public string? Replacement { get; init; }

        [JsonPropertyName("reason")]
        public string? Reason { get; init; }
    }
}
