namespace ChefAgent.Shared.Models;

/// <summary>
/// A recipe document retrieved from the vector store.
/// </summary>
public record RecipeDocument
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required List<string> Ingredients { get; init; }
    public required List<string> Directions { get; init; }
    public string? Source { get; init; }
    public string? SourceUrl { get; init; }
    public int IngredientCount { get; init; }
    public int StepCount { get; init; }
    public double? RelevanceScore { get; init; }
}

/// <summary>
/// User dietary profile and constraints.
/// </summary>
public record DietaryProfile
{
    public List<string> Allergies { get; init; } = [];
    public List<string> Restrictions { get; init; } = []; // e.g., vegetarian, vegan, halal
    public List<string> CuisinePreferences { get; init; } = [];
    public MacroTargets? MacroTargets { get; init; }
    public int? MaxPrepTimeMinutes { get; init; }
    public int HouseholdSize { get; init; } = 2;
    public string CookingSkillLevel { get; init; } = "intermediate"; // beginner, intermediate, advanced
}

public record MacroTargets
{
    public int? CaloriesPerMeal { get; init; }
    public int? ProteinGrams { get; init; }
    public int? CarbGrams { get; init; }
    public int? FatGrams { get; init; }
}

/// <summary>
/// Result of the Diet Agent validating a recipe against user constraints.
/// </summary>
public record DietaryValidation
{
    public required string RecipeId { get; init; }
    public bool IsCompatible { get; init; }
    public List<ViolationDetail> Violations { get; init; } = [];
    public List<SubstitutionSuggestion> Substitutions { get; init; } = [];
    public string? Explanation { get; init; }
}

public record ViolationDetail
{
    public required string Ingredient { get; init; }
    public required string Category { get; init; } // "dairy", "gluten", "nuts"
    public required ValidationLayer DetectedBy { get; init; }
    public string? MatchedRule { get; init; }
}

public enum ValidationLayer
{
    Rules,
    Llm,
}

/// <summary>
/// A recipe with its dietary validation attached.
/// Used in OrchestratorResponse so the UI gets per-recipe dietary badges.
/// </summary>
public record ValidatedRecipe
{
    public required RecipeDocument Recipe { get; init; }
    public DietaryValidation? Dietary { get; init; } // null = validation not run
}

public record SubstitutionSuggestion
{
    public required string OriginalIngredient { get; init; }
    public required string SuggestedReplacement { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// A meal plan for a given time period.
/// </summary>
public record MealPlan
{
    public required string Id { get; init; }
    public required List<DayPlan> Days { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public string? Notes { get; init; }
}

public record DayPlan
{
    public required DayOfWeek Day { get; init; }
    public RecipeDocument? Breakfast { get; init; }
    public RecipeDocument? Lunch { get; init; }
    public RecipeDocument? Dinner { get; init; }
    public List<string> Snacks { get; init; } = [];
}

/// <summary>
/// Classified user intent for the orchestrator.
/// </summary>
public enum UserIntent
{
    SearchRecipe,
    ValidateDiet,
    CreateMealPlan,
    ModifyMealPlan,
    GeneralQuestion,
    Unknown,
}

/// <summary>
/// The orchestrator's response combining outputs from multiple agents.
/// </summary>
public record OrchestratorResponse
{
    public required string Message { get; init; }
    public List<ValidatedRecipe> Recipes { get; init; } = []; // ← was List<RecipeDocument>
    public DietaryValidation? DietaryCheck { get; init; } // keep for ValidateDiet intent
    public MealPlan? MealPlan { get; init; }
    public UserIntent DetectedIntent { get; init; }
    public Dictionary<string, object> Metadata { get; init; } = [];
}
