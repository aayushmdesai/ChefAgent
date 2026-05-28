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

public record MealSlot
{
    public required string SlotName { get; init; } // "dinner", "lunch", "breakfast"
    public required RecipeDocument Recipe { get; init; }
    public DietaryValidation? DietaryValidation { get; init; }
    public string? ProteinCategory { get; init; } // "poultry", "beef", "fish", "vegetarian"
    public string? CuisineTag { get; init; } // "italian", "mexican", "asian", etc.
}

public record DayPlan
{
    public required string Day { get; init; } // "Monday", "Tuesday", etc.
    public required List<MealSlot> Slots { get; init; }
}

public record MealPlan
{
    public required string PlanId { get; init; } // Guid.NewGuid().ToString()
    public required List<DayPlan> Days { get; init; }
    public PlanConstraints? Constraints { get; init; }
    public DietaryProfile? Profile { get; init; }
    public DateTime CreatedAt { get; init; }
    public string Status { get; init; } = "draft"; // "draft" | "finalized"
}

public record PlanConstraints
{
    public List<string> MealSlots { get; init; } = ["dinner"]; // which meals to plan
    public int? MaxStepsWeeknight { get; init; } // e.g. 5 — fewer steps Mon–Thu
    public int? MaxStepsWeekend { get; init; } // e.g. 12 — more complex Fri–Sun
    public int HouseholdSize { get; init; } = 2;
    public bool AvoidProteinRepeat { get; init; } = true;
    public bool AvoidCuisineRepeat { get; init; } = true;
}

/// <summary>
/// Classified user intent for the orchestrator.
/// </summary>
public enum UserIntent
{
    SearchRecipe,
    ValidateDiet,
    GetMealPlan,
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
/// <summary>
/// A single turn in the conversation history.
/// Structured (Option B) so reference resolution works without LLM.
/// </summary>
public record ConversationEntry
{
    public required string Role { get; init; }          
    public required string Content { get; init; }       
    public UserIntent? Intent { get; init; }            
    public List<string> RecipeTitles { get; init; } = []; 
    public string? PlanId { get; init; }                
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
