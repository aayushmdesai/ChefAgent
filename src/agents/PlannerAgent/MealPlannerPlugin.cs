namespace ChefAgent.Agents.PlannerAgent;

using ChefAgent.Agents.Diet;
using ChefAgent.Agents.Recipe;
using ChefAgent.Shared;
using ChefAgent.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

public class MealPlannerPlugin
{
    private readonly RecipeSearchPlugin _recipeSearch;
    private readonly DietValidationPlugin _dietValidation;
    private readonly ILogger<MealPlannerPlugin> _logger;
    private readonly SessionStore _sessionStore;

    private static readonly string[] Days =
    [
        "Monday",
        "Tuesday",
        "Wednesday",
        "Thursday",
        "Friday",
        "Saturday",
        "Sunday",
    ];

    private static readonly Dictionary<string, string[]> SlotQueries = new()
    {
        ["breakfast"] =
        [
            "quick breakfast",
            "easy morning eggs",
            "healthy breakfast bowl",
            "simple pancakes",
            "oatmeal breakfast",
            "yogurt fruit breakfast",
            "toast avocado breakfast",
        ],
        ["lunch"] =
        [
            "simple lunch salad",
            "quick sandwich",
            "light soup lunch",
            "easy grain bowl",
            "pasta salad lunch",
            "wrap lunch",
            "veggie lunch plate",
        ],
        ["dinner"] =
        [
            "easy weeknight chicken dinner",
            "simple beef dinner",
            "quick pasta dinner",
            "hearty vegetable stew",
            "baked fish dinner",
            "pork tenderloin dinner",
            "bean and rice dinner",
        ],
        ["snack"] = ["healthy snack", "quick snack ideas", "fruit and nut snack"],
    };

    private static readonly Dictionary<string, string[]> ProteinKeywords = new()
    {
        ["poultry"] = ["chicken", "turkey", "duck", "hen"],
        ["beef"] = ["beef", "steak", "ground beef", "brisket", "chuck"],
        ["pork"] = ["pork", "ham", "bacon", "sausage", "prosciutto"],
        ["fish"] = ["salmon", "tuna", "cod", "tilapia", "halibut", "shrimp", "fish"],
        ["vegetarian"] = ["tofu", "tempeh", "lentil", "chickpea", "bean", "quinoa", "eggplant"],
    };

    private static readonly Dictionary<string, string[]> CuisineKeywords = new()
    {
        ["italian"] =
        [
            "pasta",
            "pizza",
            "risotto",
            "lasagna",
            "parmesan",
            "marinara",
            "fettuccine",
            "linguine",
        ],
        ["mexican"] =
        [
            "taco",
            "burrito",
            "enchilada",
            "salsa",
            "tortilla",
            "guacamole",
            "quesadilla",
            "tamale",
        ],
        ["asian"] =
        [
            "stir fry",
            "fried rice",
            "teriyaki",
            "noodle",
            "ramen",
            "curry",
            "dumpling",
            "wok",
        ],
        ["american"] =
        [
            "burger",
            "bbq",
            "casserole",
            "meatloaf",
            "mac and cheese",
            "pot pie",
            "chowder",
        ],
        ["indian"] = ["masala", "tikka", "biryani", "dal", "paneer", "naan", "chutney", "samosa"],
        ["gujarati"] = ["dhokla", "thepla", "undhiyu", "kadhi", "fafda"],
        ["punjabi"] = ["butter chicken", "sarson da saag", "makki di roti", "lassi"],
    };

    private static readonly HashSet<string> CuisineFalsePositives =
    [
        "pasta ready",
        "pasta sauce",
        "italian dressing",
        "italian mix",
        "soy sauce",
    ];

    public MealPlannerPlugin(
        RecipeSearchPlugin recipeSearch,
        DietValidationPlugin dietValidation,
        ILogger<MealPlannerPlugin> logger,
        SessionStore sessionStore
    )
    {
        _recipeSearch = recipeSearch;
        _dietValidation = dietValidation;
        _logger = logger;
        _sessionStore = sessionStore;
    }

    [KernelFunction("generate_meal_plan")]
    public async Task<MealPlan> GeneratePlanAsync(
        DietaryProfile? profile = null,
        PlanConstraints? constraints = null
    )
    {
        constraints ??= new PlanConstraints();
        var planId = Guid.NewGuid().ToString();
        var days = new List<DayPlan>();

        _logger.LogInformation(
            "Generating meal plan {PlanId} for slots: {Slots}",
            planId,
            string.Join(", ", constraints.MealSlots)
        );

        foreach (var day in Days)
        {
            var slots = new List<MealSlot>();
            var isWeeknight = IsWeeknight(day);

            foreach (var slotName in constraints.MealSlots)
            {
                var slot = await GenerateSlotAsync(
                    day,
                    slotName,
                    profile,
                    constraints,
                    isWeeknight,
                    days
                );
                slots.Add(slot);
            }

            days.Add(new DayPlan { Day = day, Slots = slots });
            _logger.LogInformation("Day {Day} complete — {Count} slot(s)", day, slots.Count);
        }

        return new MealPlan
        {
            PlanId = planId,
            Days = days,
            Constraints = constraints,
            Profile = profile,
        };
    }

    public async Task<(MealPlan Plan, string Message)> ModifyPlanAsync(
        string sessionId,
        string targetDay,
        string targetSlot = "dinner",
        string? constraint = null
    )
    {
        var plan =
            await _sessionStore.GetPlanAsync(sessionId)
            ?? throw new InvalidOperationException(
                $"No plan found for session '{sessionId}'. Generate a plan first."
            );

        var dayIndex = Array.IndexOf(Days, targetDay);
        if (dayIndex == -1)
            throw new ArgumentException($"Unknown day '{targetDay}'. Use Monday–Sunday.");

        var query = BuildModifyQuery(targetDay, targetSlot, constraint);
        var isWeeknight = IsWeeknight(targetDay);
        var maxSteps =
            constraint?.Contains("simpler", StringComparison.OrdinalIgnoreCase) == true
                ? 5
                : (
                    isWeeknight
                        ? plan.Constraints?.MaxStepsWeeknight
                        : plan.Constraints?.MaxStepsWeekend
                );

        _logger.LogInformation(
            "Modifying {Day} {Slot} — query: '{Query}', constraint: '{Constraint}'",
            targetDay,
            targetSlot,
            query,
            constraint ?? "none"
        );

        // Get current recipe title to avoid returning the same recipe
        var currentTitle = plan
            .Days.FirstOrDefault(d => d.Day == targetDay)
            ?.Slots.FirstOrDefault(s => s.SlotName == targetSlot)
            ?.Recipe.Title;

        var candidates = await _recipeSearch.SearchRecipesAsync(
            query,
            maxResults: 5,
            maxSteps: maxSteps
        );

        var newRecipe = SelectWithVarietyForModify(
            candidates,
            plan,
            targetDay,
            targetSlot,
            currentTitle
        );

        DietaryValidation? validation = null;
        if (plan.Profile is not null)
            validation = await _dietValidation.ValidateRecipeAsync(newRecipe, plan.Profile);

        var newSlot = new MealSlot
        {
            SlotName = targetSlot,
            Recipe = newRecipe,
            DietaryValidation = validation,
            ProteinCategory = InferProteinCategory(newRecipe),
            CuisineTag = InferCuisineTag(newRecipe),
        };

        // Immutable update — map over days and slots
        var updatedDays = plan
            .Days.Select(d =>
                d.Day != targetDay
                    ? d
                    : d with
                    {
                        Slots = d
                            .Slots.Select(s => s.SlotName != targetSlot ? s : newSlot)
                            .ToList(),
                    }
            )
            .ToList();

        var updatedPlan = plan with { Days = updatedDays };
        await _sessionStore.SavePlanAsync(sessionId, updatedPlan);

        var message = BuildModifyMessage(targetDay, targetSlot, newRecipe.Title, constraint);
        return (updatedPlan, message);
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private async Task<MealSlot> GenerateSlotAsync(
        string day,
        string slotName,
        DietaryProfile? profile,
        PlanConstraints constraints,
        bool isWeeknight,
        List<DayPlan> plannedSoFar
    )
    {
        var dayIndex = Array.IndexOf(Days, day);
        var queries = SlotQueries.GetValueOrDefault(slotName, SlotQueries["dinner"]);
        var query = queries[dayIndex % queries.Length];
        var maxSteps = isWeeknight ? constraints.MaxStepsWeeknight : constraints.MaxStepsWeekend;

        var candidates = await _recipeSearch.SearchRecipesAsync(
            query,
            maxResults: 5,
            maxSteps: maxSteps
        );
        var recipe = SelectWithVariety(candidates, plannedSoFar, constraints);

        DietaryValidation? validation = null;
        if (profile is not null)
        {
            validation = await _dietValidation.ValidateRecipeAsync(recipe, profile);
            _logger.LogDebug(
                "{Day} {Slot}: {Title} — compatible: {Compatible}",
                day,
                slotName,
                recipe.Title,
                validation.IsCompatible
            );
        }

        return new MealSlot
        {
            SlotName = slotName,
            Recipe = recipe,
            DietaryValidation = validation,
            ProteinCategory = InferProteinCategory(recipe),
            CuisineTag = InferCuisineTag(recipe),
        };
    }

    private RecipeDocument SelectWithVariety(
        List<RecipeDocument> candidates,
        List<DayPlan> plannedSoFar,
        PlanConstraints constraints
    )
    {
        var recentProteins = plannedSoFar
            .TakeLast(2)
            .SelectMany(d => d.Slots)
            .Select(s => s.ProteinCategory)
            .Where(p => p is not null)
            .ToHashSet();

        var recentCuisines = plannedSoFar
            .TakeLast(2)
            .SelectMany(d => d.Slots)
            .Select(s => s.CuisineTag)
            .Where(c => c is not null)
            .ToHashSet();

        foreach (var candidate in candidates)
        {
            var protein = InferProteinCategory(candidate);
            var cuisine = InferCuisineTag(candidate);
            if (constraints.AvoidProteinRepeat && recentProteins.Contains(protein))
                continue;
            if (constraints.AvoidCuisineRepeat && recentCuisines.Contains(cuisine))
                continue;
            return candidate;
        }

        _logger.LogDebug("Variety fallback — returning top candidate");
        return candidates[0];
    }

    private static string BuildModifyQuery(string day, string slot, string? constraint)
    {
        if (string.IsNullOrWhiteSpace(constraint))
        {
            var queries = SlotQueries.GetValueOrDefault(slot, SlotQueries["dinner"]);
            var dayIndex = Array.IndexOf(Days, day);
            // Offset by 3 to get a meaningfully different query than the original
            return queries[(dayIndex + 3) % queries.Length];
        }

        var cleaned = constraint
            .Replace("something with", "", StringComparison.OrdinalIgnoreCase)
            .Replace("something", "", StringComparison.OrdinalIgnoreCase)
            .Replace("simpler", "simple quick", StringComparison.OrdinalIgnoreCase)
            .Trim();

        return $"{cleaned} {slot}".Trim();
    }

    private RecipeDocument SelectWithVarietyForModify(
        List<RecipeDocument> candidates,
        MealPlan plan,
        string targetDay,
        string targetSlot,
        string? excludeTitle = null
    )
    {
        var neighborDays = plan
            .Days.Where(d => d.Day != targetDay)
            .OrderBy(d => Math.Abs(Array.IndexOf(Days, d.Day) - Array.IndexOf(Days, targetDay)))
            .Take(2);

        var neighborProteins = neighborDays
            .SelectMany(d => d.Slots)
            .Select(s => s.ProteinCategory)
            .Where(p => p is not null)
            .ToHashSet();

        var neighborCuisines = neighborDays
            .SelectMany(d => d.Slots)
            .Select(s => s.CuisineTag)
            .Where(c => c is not null)
            .ToHashSet();

        foreach (var candidate in candidates)
        {
            // Always skip the current recipe
            if (
                excludeTitle is not null
                && candidate.Title.Equals(excludeTitle, StringComparison.OrdinalIgnoreCase)
            )
                continue;

            var protein = InferProteinCategory(candidate);
            var cuisine = InferCuisineTag(candidate);
            if (neighborProteins.Contains(protein))
                continue;
            if (neighborCuisines.Contains(cuisine))
                continue;
            return candidate;
        }

        // Fallback — at least return something different from current
        var different = candidates.FirstOrDefault(c =>
            !c.Title.Equals(excludeTitle, StringComparison.OrdinalIgnoreCase)
        );

        _logger.LogDebug("Modify variety fallback — returning first different candidate");
        return different ?? candidates[0];
    }

    private static string BuildModifyMessage(
        string day,
        string slot,
        string recipeTitle,
        string? constraint
    )
    {
        var constraintNote = string.IsNullOrWhiteSpace(constraint) ? "" : $" ({constraint})";
        return $"Swapped {day}'s {slot} to {recipeTitle}{constraintNote}.";
    }

    private static string? InferProteinCategory(RecipeDocument recipe)
    {
        var text =
            $"{recipe.Title} {string.Join(" ", recipe.Ingredients ?? [])}".ToLowerInvariant();
        foreach (var (category, keywords) in ProteinKeywords)
            if (keywords.Any(k => text.Contains(k)))
                return category;
        return null;
    }

    private static string? InferCuisineTag(RecipeDocument recipe)
    {
        var ingredients = string.Join(" ", recipe.Ingredients ?? []).ToLowerInvariant();
        var title = recipe.Title.ToLowerInvariant();

        var falsePositive = CuisineFalsePositives.FirstOrDefault(fp => ingredients.Contains(fp));
        if (falsePositive is not null)
            return null;

        var text = $"{title} {ingredients}";
        foreach (var (tag, keywords) in CuisineKeywords)
            if (keywords.Any(k => text.Contains(k)))
                return tag;
        return null;
    }

    private static bool IsWeeknight(string day) =>
        day is "Monday" or "Tuesday" or "Wednesday" or "Thursday";
}
