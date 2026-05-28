namespace ChefAgent.Agents.Orchestrator;

using System.Text;
using System.Text.Json;
using ChefAgent.Agents.Diet;
using ChefAgent.Agents.PlannerAgent;
using ChefAgent.Agents.Recipe;
using ChefAgent.Shared;
using ChefAgent.Shared.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Coordinates agent calls based on classified intent.
/// Takes a ClassifiedIntent from IntentRouter and routes to the right agent(s),
/// merges results, and builds a human-readable OrchestratorResponse.
///
/// Routing flows:
///   SearchRecipe (no profile)   → Recipe Agent only
///   SearchRecipe (with profile) → Recipe Agent → Diet Agent → sort compatible first
///   ValidateDiet                → Diet Agent only
///   CreateMealPlan              → placeholder (Month 2)
///   ModifyMealPlan              → placeholder (Month 2)
///   GeneralQuestion             → Ollama direct (conversational)
///   Unknown                     → ask user to clarify
///
/// Failure handling:
///   Recipe Agent fails          → helpful error, no recipes
///   Diet Agent LLM timeout      → recipes returned without validation + warning
///   Intent LLM timeout          → Unknown intent, ask to clarify
///   All agents fail             → graceful error, never 500
/// </summary>
public class AgentOrchestrator
{
    private readonly RecipeSearchPlugin _recipeAgent;
    private readonly DietValidationPlugin _dietAgent;
    private readonly MealPlannerPlugin _plannerAgent;
    private readonly SessionStore _sessionStore;
    private readonly HttpClient _httpClient;
    private readonly string _ollamaUrl;
    private readonly string _chatModel;
    private readonly ILogger<AgentOrchestrator> _logger;

    public AgentOrchestrator(
        RecipeSearchPlugin recipeAgent,
        DietValidationPlugin dietAgent,
        MealPlannerPlugin plannerAgent,
        SessionStore sessionStore,
        HttpClient httpClient,
        string ollamaUrl,
        string chatModel,
        ILogger<AgentOrchestrator> logger
    )
    {
        _recipeAgent = recipeAgent;
        _dietAgent = dietAgent;
        _httpClient = httpClient;
        _sessionStore = sessionStore;
        _plannerAgent = plannerAgent;
        _ollamaUrl = ollamaUrl;
        _chatModel = chatModel;
        _logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Main entry point. Takes a classified intent and routes to the right agent(s).
    /// Always returns an OrchestratorResponse — never throws to the caller.
    /// </summary>
    public async Task<OrchestratorResponse> RouteAsync(ClassifiedIntent classified)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        _logger.LogInformation(
            "[Orchestrator] Intent={Intent} ClassifiedBy={By} Query='{Query}' Profile={HasProfile}",
            classified.Intent,
            classified.ClassifiedBy,
            classified.SearchQuery,
            classified.MergedProfile is not null
        );

        // ── Step 1: Save user message to history ─────────────────
        if (!string.IsNullOrEmpty(classified.SessionId))
        {
            await _sessionStore.AppendMessageAsync(classified.SessionId, new ConversationEntry
            {
                Role = "user",
                Content = classified.OriginalMessage,
            });
        }

        // ── Step 2: Load + merge + persist profile ────────────────
        if (!string.IsNullOrEmpty(classified.SessionId))
        {
            var mergedProfile = await LoadAndMergeProfileAsync(
                classified.SessionId,
                classified.MergedProfile);

            if (mergedProfile is not null)
                classified = classified with { MergedProfile = mergedProfile };
        }

        // ── Step 3: Resolve references using history ──────────────
        classified = await ResolveReferencesAsync(classified);

        // ── Step 4: Route to the right handler ───────────────────
        var response = classified.Intent switch
        {
            UserIntent.SearchRecipe => await HandleSearchRecipeAsync(classified),
            UserIntent.ValidateDiet => await HandleValidateDietAsync(classified),
            UserIntent.CreateMealPlan => await HandleCreateMealPlanAsync(classified),
            UserIntent.ModifyMealPlan => await HandleModifyMealPlanAsync(classified),
            UserIntent.GeneralQuestion => await HandleGeneralQuestionAsync(classified),
            _ => HandleUnknown(classified),
        };

        // ── Step 5: Save assistant response to history ────────────
        if (!string.IsNullOrEmpty(classified.SessionId))
        {
            await _sessionStore.AppendMessageAsync(classified.SessionId, new ConversationEntry
            {
                Role = "assistant",
                Content = response.Message,
                Intent = response.DetectedIntent,
                RecipeTitles = response.Recipes.Select(r => r.Recipe.Title).Take(5).ToList(),
                PlanId = response.MealPlan?.PlanId,
            });
        }

        sw.Stop();
        _logger.LogInformation(
            "[Orchestrator] Intent={Intent} Time={Ms}ms Recipes={Count}",
            classified.Intent,
            sw.ElapsedMilliseconds,
            response.Recipes.Count
        );

        return response;
    }
    // ── Reference Resolution ──────────────────────────────────────────────────

    private static readonly HashSet<string> ReferenceWords =
    [
        "it", "that", "this", "the first", "the second", "the third",
    "first one", "second one", "third one",
    "that one", "this one", "the one",
    "again", "same", "that recipe", "that dish",
    ];
    // ── Profile Persistence ───────────────────────────────────────────────────

    /// <summary>
    /// Loads stored profile from Redis, merges with request profile (union, request wins),
    /// saves merged profile back. Returns the merged profile.
    /// </summary>
    private async Task<DietaryProfile?> LoadAndMergeProfileAsync(
        string sessionId,
        DietaryProfile? requestProfile)
    {
        DietaryProfile? storedProfile = null;
        try
        {
            storedProfile = await _sessionStore.GetProfileAsync(sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Orchestrator] Failed to load profile for session {SessionId}", sessionId);
        }

        // Nothing to merge — no stored, no request
        if (storedProfile is null && requestProfile is null)
            return null;

        // Only one side exists — use it directly, still save to ensure TTL refresh
        var merged = storedProfile is null ? requestProfile!
            : requestProfile is null ? storedProfile
            : new DietaryProfile
            {
                // Union merge — request wins on conflicts via Concat order + Distinct
                Restrictions = requestProfile.Restrictions
                    .Union(storedProfile.Restrictions, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Allergies = requestProfile.Allergies
                    .Union(storedProfile.Allergies, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                CuisinePreferences = requestProfile.CuisinePreferences
                    .Union(storedProfile.CuisinePreferences, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };

        // Save merged back — this is what persists for future turns
        try
        {
            await _sessionStore.SaveProfileAsync(sessionId, merged);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Orchestrator] Failed to save merged profile for session {SessionId}", sessionId);
        }

        return merged;
    }

    /// <summary>
    /// Detects implicit references in the user message and injects context from history.
    /// Examples:
    ///   "is the first one vegan?" → injects top recipe title from last SearchRecipe turn
    ///   "swap it again"           → injects last ModifyMealPlan's target day
    /// </summary>
    private async Task<ClassifiedIntent> ResolveReferencesAsync(ClassifiedIntent classified)
    {
        if (string.IsNullOrEmpty(classified.SessionId))
            return classified;

        var lower = classified.OriginalMessage.ToLowerInvariant();
        var hasReference = ReferenceWords.Any(w => lower.Contains(w));

        if (!hasReference)
            return classified;

        List<ConversationEntry> history;
        try
        {
            history = await _sessionStore.GetHistoryAsync(classified.SessionId, limit: 6);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Orchestrator] History load failed — proceeding without context");
            return classified;
        }

        if (history.Count == 0)
            return classified;

        // Find the most recent assistant entry
        var lastAssistant = history.LastOrDefault(e => e.Role == "assistant");
        if (lastAssistant is null)
            return classified;

        _logger.LogInformation(
            "[Orchestrator] Reference detected in '{Msg}' — last intent was {Intent}, recipes={Recipes}",
            classified.OriginalMessage,
            lastAssistant.Intent,
            string.Join(", ", lastAssistant.RecipeTitles)
        );

        // ── Recipe reference: "is it vegan?", "the first one" ────
        if (lastAssistant.Intent == UserIntent.SearchRecipe && lastAssistant.RecipeTitles.Count > 0)
        {
            var targetRecipe = lower.Contains("second") || lower.Contains("2nd")
                ? lastAssistant.RecipeTitles.ElementAtOrDefault(1)
                : lower.Contains("third") || lower.Contains("3rd")
                    ? lastAssistant.RecipeTitles.ElementAtOrDefault(2)
                    : lastAssistant.RecipeTitles[0]; // "it", "that", "first" → top result

            if (targetRecipe is not null)
            {
                _logger.LogInformation("[Orchestrator] Resolved recipe reference → '{Title}'", targetRecipe);
                return classified with
                {
                    SearchQuery = targetRecipe,
                    Intent = (classified.Intent == UserIntent.Unknown
              || classified.Intent == UserIntent.SearchRecipe)
            ? UserIntent.ValidateDiet
            : classified.Intent,
                    };
            }
        }

        // ── Plan reference: "swap it again" ──────────────────────
        if (lastAssistant.Intent == UserIntent.ModifyMealPlan
            && lower.Contains("again")
            && string.IsNullOrEmpty(classified.TargetDay))
        {
            // Find the last user message that had a TargetDay — re-extract it
            var lastUserModify = history
                .Where(e => e.Role == "user")
                .LastOrDefault();

            // Can't recover the day from raw content reliably — ask clarifying question
            // The key win here is that intent is now ModifyMealPlan, not Unknown
            return classified with { Intent = UserIntent.ModifyMealPlan };
        }

        return classified;
    }
    // ── Intent Handlers ───────────────────────────────────────────────────────
    private async Task<OrchestratorResponse> HandleSearchRecipeAsync(ClassifiedIntent classified)
    {
        // Step 1 — Recipe Agent
        List<RecipeDocument> recipes;
        try
        {
            recipes = await _recipeAgent.SearchRecipesAsync(classified.SearchQuery, maxResults: 5);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recipe Agent failed for query '{Query}'", classified.SearchQuery);
            return ErrorResponse(
                classified,
                "Sorry — I couldn't search for recipes right now. Please try again."
            );
        }

        if (recipes.Count == 0)
            return new OrchestratorResponse
            {
                Message =
                    $"I couldn't find any recipes for \"{classified.SearchQuery}\". Try a different query.",
                DetectedIntent = UserIntent.SearchRecipe,
                Recipes = [],
                Metadata = BuildMetadata(classified, dietaryApplied: false),
            };

        // Step 2 — Diet Agent (only if profile present)
        if (
            classified.MergedProfile is null
            || (
                classified.MergedProfile.Allergies.Count == 0
                && classified.MergedProfile.Restrictions.Count == 0
            )
        )
        {
            return new OrchestratorResponse
            {
                Message = BuildSearchMessage(recipes, classified, dietaryApplied: false),
                DetectedIntent = UserIntent.SearchRecipe,
                Recipes = recipes.Select(r => new ValidatedRecipe { Recipe = r }).ToList(),
                Metadata = BuildMetadata(classified, dietaryApplied: false),
            };
        }

        // Profile present → validate each recipe
        var dietaryResults = new List<ValidatedRecipe>();
        var dietaryUnavailable = false;

        foreach (var recipe in recipes)
        {
            try
            {
                var validation = await _dietAgent.ValidateRecipeAsync(
                    recipe,
                    classified.MergedProfile
                );
                dietaryResults.Add(new ValidatedRecipe { Recipe = recipe, Dietary = validation });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Diet Agent failed for '{Title}' — including without validation",
                    recipe.Title
                );
                dietaryUnavailable = true;
                dietaryResults.Add(new ValidatedRecipe { Recipe = recipe, Dietary = null });
            }
        }

        // Sort: compatible first, incompatible after
        var sorted = dietaryResults
            .OrderByDescending(r => r.Dietary?.IsCompatible ?? false)
            .ToList();

        var compatibleCount = sorted.Count(r => r.Dietary?.IsCompatible == true);

        return new OrchestratorResponse
        {
            Message = BuildSearchMessage(
                recipes,
                classified,
                dietaryApplied: true,
                compatibleCount: compatibleCount,
                total: sorted.Count,
                dietaryUnavailable: dietaryUnavailable
            ),
            DetectedIntent = UserIntent.SearchRecipe,
            Recipes = sorted,

            Metadata = BuildMetadata(
                classified,
                dietaryApplied: true,
                compatibleCount: compatibleCount,
                dietaryUnavailable: dietaryUnavailable
            ),
        };
    }

    private async Task<OrchestratorResponse> HandleValidateDietAsync(ClassifiedIntent classified)
    {
        // ValidateDiet without a specific recipe — search first, then validate top result
        if (
            classified.MergedProfile is null
            || (
                classified.MergedProfile.Allergies.Count == 0
                && classified.MergedProfile.Restrictions.Count == 0
            )
        )
        {
            return new OrchestratorResponse
            {
                Message =
                    "I'd be happy to check a recipe for you — could you tell me which recipe and any dietary restrictions you have?",
                DetectedIntent = UserIntent.ValidateDiet,
                Recipes = [],
                Metadata = BuildMetadata(classified, dietaryApplied: false),
            };
        }

        // Search for the recipe mentioned in the query
        List<RecipeDocument> recipes;
        try
        {
            recipes = await _recipeAgent.SearchRecipesAsync(classified.SearchQuery, maxResults: 1);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Recipe search failed during ValidateDiet for '{Query}'",
                classified.SearchQuery
            );
            return ErrorResponse(classified, "Sorry — I couldn't find that recipe right now.");
        }

        if (recipes.Count == 0)
        {
            return new OrchestratorResponse
            {
                Message =
                    $"I couldn't find a recipe matching \"{classified.SearchQuery}\" to validate.",
                DetectedIntent = UserIntent.ValidateDiet,
                Recipes = [],
                Metadata = BuildMetadata(classified, dietaryApplied: false),
            };
        }

        var recipe = recipes[0];
        DietaryValidation validation;

        try
        {
            validation = await _dietAgent.ValidateRecipeAsync(recipe, classified.MergedProfile);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Diet Agent failed during ValidateDiet for '{Title}'",
                recipe.Title
            );
            return new OrchestratorResponse
            {
                Message =
                    $"Found \"{recipe.Title}\" but dietary validation is unavailable right now. Please review manually.",
                DetectedIntent = UserIntent.ValidateDiet,
                Recipes = [new ValidatedRecipe { Recipe = recipe, Dietary = null }],
                Metadata = BuildMetadata(classified, dietaryApplied: false),
            };
        }

        var resultMessage = validation.IsCompatible
            ? $"\"{recipe.Title}\" looks compatible with your dietary profile. {validation.Explanation}"
            : $"\"{recipe.Title}\" has some issues for your profile. {validation.Explanation}";

        if (!validation.IsCompatible && validation.Substitutions.Count > 0)
        {
            var subs = string.Join(
                ", ",
                validation
                    .Substitutions.Take(2)
                    .Select(s => $"{s.OriginalIngredient} → {s.SuggestedReplacement}")
            );
            resultMessage += $" Suggested swaps: {subs}.";
        }

        return new OrchestratorResponse
        {
            Message = resultMessage,
            DetectedIntent = UserIntent.ValidateDiet,
            Recipes = [new ValidatedRecipe { Recipe = recipe, Dietary = validation }],
            DietaryCheck = validation,
            Metadata = BuildMetadata(classified, dietaryApplied: true),
        };
    }

    private async Task<OrchestratorResponse> HandleCreateMealPlanAsync(ClassifiedIntent classified)
    {
        var sessionId = classified.SessionId;
        if (string.IsNullOrEmpty(sessionId))
            return new OrchestratorResponse
            {
                Message =
                    "I need a session ID to save your meal plan. Please include one in your request.",
                DetectedIntent = UserIntent.CreateMealPlan,
                Recipes = [],
                Metadata = BuildMetadata(classified, dietaryApplied: false),
            };

        try
        {
            var constraints = new PlanConstraints { MealSlots = classified.MealSlots };
            var plan = await _plannerAgent.GeneratePlanAsync(classified.MergedProfile, constraints);
            await _sessionStore.SavePlanAsync(sessionId, plan);

            _logger.LogInformation(
                "[Orchestrator] Meal plan {PlanId} generated for session {SessionId}",
                plan.PlanId,
                sessionId
            );

            var slotsDesc =
                classified.MealSlots.Count == 3
                    ? "full day (breakfast, lunch, and dinner)"
                    : string.Join(" and ", classified.MealSlots);

            var profileDesc = classified.MergedProfile is null
                ? ""
                : $" tailored to your {string.Join(", ", classified.MergedProfile.Restrictions.Concat(classified.MergedProfile.Allergies))} profile";

            return new OrchestratorResponse
            {
                Message =
                    $"Here's your 7-day {slotsDesc} plan{profileDesc}. You can ask me to swap any day — just say \"swap Tuesday dinner to something with pasta\".",
                DetectedIntent = UserIntent.CreateMealPlan,
                Recipes = [],
                MealPlan = plan,
                Metadata = BuildMetadata(
                    classified,
                    dietaryApplied: classified.MergedProfile is not null
                ),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Planner Agent failed for session {SessionId}", sessionId);
            return ErrorResponse(
                classified,
                "Sorry — I couldn't generate your meal plan right now. Please try again."
            );
        }
    }

    private async Task<OrchestratorResponse> HandleModifyMealPlanAsync(ClassifiedIntent classified)
    {
        var sessionId = classified.SessionId;
        if (string.IsNullOrEmpty(sessionId))
            return new OrchestratorResponse
            {
                Message =
                    "I need a session ID to find your existing plan. Please include one in your request.",
                DetectedIntent = UserIntent.ModifyMealPlan,
                Recipes = [],
                Metadata = BuildMetadata(classified, dietaryApplied: false),
            };

        // Extract target day from the classified intent — fall back to asking
        var targetDay = classified.TargetDay;
        if (string.IsNullOrEmpty(targetDay))
            return new OrchestratorResponse
            {
                Message =
                    "Which day would you like to swap? Say something like \"swap Tuesday\" or \"change Wednesday to pasta\".",
                DetectedIntent = UserIntent.ModifyMealPlan,
                Recipes = [],
                Metadata = BuildMetadata(classified, dietaryApplied: false),
            };

        try
        {
            var (plan, message) = await _plannerAgent.ModifyPlanAsync(
                sessionId,
                targetDay,
                classified.TargetSlot,
                classified.ModifyConstraint
            );

            return new OrchestratorResponse
            {
                Message = message,
                DetectedIntent = UserIntent.ModifyMealPlan,
                Recipes = [],
                MealPlan = plan,
                Metadata = BuildMetadata(classified, dietaryApplied: false),
            };
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Invalid operation for session {SessionId}", sessionId);
            return new OrchestratorResponse
            {
                Message =
                    "I couldn't find your meal plan. Try generating one first by saying \"plan my dinners for the week\".",
                DetectedIntent = UserIntent.ModifyMealPlan,
                Recipes = [],
                Metadata = BuildMetadata(classified, dietaryApplied: false),
            };
        }
        catch (ArgumentException ex)
        {
            return new OrchestratorResponse
            {
                Message = ex.Message,
                DetectedIntent = UserIntent.ModifyMealPlan,
                Recipes = [],
                Metadata = BuildMetadata(classified, dietaryApplied: false),
            };
        }
    }

    private async Task<OrchestratorResponse> HandleGeneralQuestionAsync(ClassifiedIntent classified)
    {
        try
        {
            var answer = await AskOllamaAsync(classified.SearchQuery);
            return new OrchestratorResponse
            {
                Message = answer,
                DetectedIntent = UserIntent.GeneralQuestion,
                Recipes = [],
                Metadata = BuildMetadata(classified, dietaryApplied: false),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Ollama failed for GeneralQuestion '{Query}'",
                classified.SearchQuery
            );
            return new OrchestratorResponse
            {
                Message =
                    "I couldn't answer that right now — my reasoning engine is unavailable. Try a recipe search instead.",
                DetectedIntent = UserIntent.GeneralQuestion,
                Recipes = [],
                Metadata = BuildMetadata(classified, dietaryApplied: false),
            };
        }
    }

    private static OrchestratorResponse HandlePlaceholder(
        string featureName,
        ClassifiedIntent classified
    )
    {
        var deferredMsg =
            classified.DeferredMessage
            ?? $"{featureName} is coming in Month 2. Let me help you find a recipe for now.";

        return new OrchestratorResponse
        {
            Message = deferredMsg,
            DetectedIntent = classified.Intent,
            Recipes = [],
            Metadata = BuildMetadata(classified, dietaryApplied: false),
        };
    }

    private static OrchestratorResponse HandleUnknown(ClassifiedIntent classified)
    {
        return new OrchestratorResponse
        {
            Message =
                "I'm not sure what you're looking for. Try asking me to find a recipe, check if a dish is safe for your diet, or ask a cooking question.",
            DetectedIntent = UserIntent.Unknown,
            Recipes = [],
            Metadata = BuildMetadata(classified, dietaryApplied: false),
        };
    }

    // ── Response Message Templates ────────────────────────────────────────────

    private static string BuildSearchMessage(
        List<RecipeDocument> recipes,
        ClassifiedIntent classified,
        bool dietaryApplied,
        int compatibleCount = 0,
        int total = 0,
        bool dietaryUnavailable = false
    )
    {
        var sb = new StringBuilder();
        var query = classified.SearchQuery;
        var count = recipes.Count;

        if (!dietaryApplied)
        {
            sb.Append($"Here are {count} recipes for \"{query}\".");
        }
        else
        {
            var profile = classified.MergedProfile;
            var restrictions = profile?.Restrictions ?? [];
            var allergies = profile?.Allergies ?? [];
            var profileDesc = string.Join(", ", restrictions.Concat(allergies));

            if (compatibleCount == count)
            {
                sb.Append($"Here are {count} {profileDesc}-friendly recipes for \"{query}\".");
            }
            else if (compatibleCount == 0)
            {
                sb.Append(
                    $"Found {count} recipes for \"{query}\" but none fully matched your {profileDesc} profile."
                );
                sb.Append(" Check the details for substitution suggestions.");
            }
            else
            {
                sb.Append(
                    $"Found {count} recipes for \"{query}\". {compatibleCount} are compatible with your {profileDesc} profile."
                );
                sb.Append(" The rest have notes — check the details for substitution suggestions.");
            }

            if (dietaryUnavailable)
                sb.Append(
                    " Note: dietary check was unavailable for some recipes — please review manually."
                );
        }

        // Append deferred message if any
        if (!string.IsNullOrEmpty(classified.DeferredMessage))
        {
            sb.Append($" {classified.DeferredMessage}");
        }

        return sb.ToString();
    }

    // ── Ollama Direct (GeneralQuestion) ───────────────────────────────────────

    private async Task<string> AskOllamaAsync(string question)
    {
        var payload = new
        {
            model = _chatModel,
            stream = false,
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = "You are a helpful cooking assistant. Answer questions about food, cooking techniques, and ingredients concisely. Keep answers under 3 sentences.",
                },
                new { role = "user", content = question },
            },
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync($"{_ollamaUrl}/api/chat", content);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("message").GetProperty("content").GetString()
            ?? string.Empty;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static OrchestratorResponse ErrorResponse(
        ClassifiedIntent classified,
        string message
    ) =>
        new()
        {
            Message = message,
            DetectedIntent = classified.Intent,
            Recipes = [],
            Metadata = BuildMetadata(classified, dietaryApplied: false),
        };

    private static Dictionary<string, object> BuildMetadata(
        ClassifiedIntent classified,
        bool dietaryApplied,
        int compatibleCount = 0,
        bool dietaryUnavailable = false
    )
    {
        var metadata = new Dictionary<string, object>
        {
            ["intentClassifiedBy"] = classified.ClassifiedBy,
            ["dietaryValidationApplied"] = dietaryApplied,
        };

        if (dietaryApplied)
            metadata["compatibleCount"] = compatibleCount;

        if (dietaryUnavailable)
            metadata["dietaryUnavailable"] = true;

        if (classified.DeferredIntents.Count > 0)
        {
            metadata["deferredIntents"] = classified
                .DeferredIntents.Select(i => i.ToString())
                .ToList();
            metadata["deferredMessage"] = classified.DeferredMessage ?? "";
        }

        return metadata;
    }
}
