namespace ChefAgent.Agents.PlannerAgent;

using ChefAgent.Agents.Diet;
using ChefAgent.Agents.Recipe;
using ChefAgent.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

public class MealPlannerPlugin
{
    private readonly RecipeSearchPlugin _recipeSearch;
    private readonly DietValidationPlugin _dietValidation;
    private readonly ILogger<MealPlannerPlugin> _logger;

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

    // Protein keyword map for variety enforcement (Day 4)
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
        ["italian"] = ["pasta", "pizza", "risotto", "lasagna", "parmesan", "marinara"],
        ["mexican"] = ["taco", "burrito", "enchilada", "salsa", "tortilla", "guacamole"],
        ["asian"] = ["stir fry", "fried rice", "soy sauce", "teriyaki", "noodle", "ramen", "curry"],
        ["american"] = ["burger", "bbq", "casserole", "meatloaf", "mac and cheese"],
    };

    public MealPlannerPlugin(
        RecipeSearchPlugin recipeSearch,
        DietValidationPlugin dietValidation,
        ILogger<MealPlannerPlugin> logger
    )
    {
        _recipeSearch = recipeSearch;
        _dietValidation = dietValidation;
        _logger = logger;
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

    // --- Private helpers ---

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
        var query = queries[dayIndex % queries.Length]; // rotate queries by day

        var maxSteps = isWeeknight ? constraints.MaxStepsWeeknight : constraints.MaxStepsWeekend;

        // Fetch a few extra candidates so variety selection has room to pick
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
        // Collect protein/cuisine used in the last 2 days
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

        // Fallback — no variety winner, take best match
        _logger.LogDebug("Variety fallback — returning top candidate");
        return candidates[0];
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
        var text =
            $"{recipe.Title} {string.Join(" ", recipe.Ingredients ?? [])}".ToLowerInvariant();
        foreach (var (tag, keywords) in CuisineKeywords)
            if (keywords.Any(k => text.Contains(k)))
                return tag;
        return null;
    }

    private static bool IsWeeknight(string day) =>
        day is "Monday" or "Tuesday" or "Wednesday" or "Thursday";
}
