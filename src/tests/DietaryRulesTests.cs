using ChefAgent.Agents.Diet;
using ChefAgent.Shared.Models;
using Xunit;

namespace ChefAgent.Tests;

/// <summary>
/// Unit tests for DietaryRules.Validate — pure static logic, no external dependencies.
/// </summary>
public class DietaryRulesTests
{
    // ── Nut Allergy ───────────────────────────────────────────────────

    [Fact]
    public void NutAllergy_PeanutIngredient_HasViolations()
    {
        var violations = DietaryRules.Validate(
            ["peanut butter", "flour", "sugar"],
            new DietaryProfile { Allergies = ["nuts"] }
        );
        Assert.NotEmpty(violations);
    }

    [Fact]
    public void NutAllergy_AlmondIngredient_HasViolations()
    {
        var violations = DietaryRules.Validate(
            ["almond flour", "eggs", "butter"],
            new DietaryProfile { Allergies = ["nuts"] }
        );
        Assert.NotEmpty(violations);
    }

    [Fact]
    public void NutAllergy_NoNutIngredients_Clean()
    {
        var violations = DietaryRules.Validate(
            ["pasta", "tomatoes", "garlic", "olive oil"],
            new DietaryProfile { Allergies = ["nuts"] }
        );
        Assert.Empty(violations);
    }

    // ── Dairy Restriction ─────────────────────────────────────────────

    [Fact]
    public void DairyFree_CheeseIngredient_HasViolations()
    {
        var violations = DietaryRules.Validate(
            ["mozzarella cheese", "tomato sauce", "pasta"],
            new DietaryProfile { Restrictions = ["dairy-free"] }
        );
        Assert.NotEmpty(violations);
    }

    [Fact]
    public void DairyFree_MilkIngredient_HasViolations()
    {
        var violations = DietaryRules.Validate(
            ["whole milk", "eggs", "flour"],
            new DietaryProfile { Restrictions = ["dairy-free"] }
        );
        Assert.NotEmpty(violations);
    }

    [Fact]
    public void DairyFree_ButterIngredient_HasViolations()
    {
        var violations = DietaryRules.Validate(
            ["unsalted butter", "sugar", "vanilla"],
            new DietaryProfile { Restrictions = ["dairy-free"] }
        );
        Assert.NotEmpty(violations);
    }

    [Fact]
    public void DairyFree_NoDairy_Clean()
    {
        var violations = DietaryRules.Validate(
            ["olive oil", "garlic", "lemon juice", "herbs"],
            new DietaryProfile { Restrictions = ["dairy-free"] }
        );
        Assert.Empty(violations);
    }

    // ── Gluten Restriction ────────────────────────────────────────────

    [Fact]
    public void GlutenFree_WheatFlour_HasViolations()
    {
        var violations = DietaryRules.Validate(
            ["wheat flour", "eggs", "sugar"],
            new DietaryProfile { Restrictions = ["gluten-free"] }
        );
        Assert.NotEmpty(violations);
    }

    [Fact]
    public void GlutenFree_Pasta_HasViolations()
    {
        var violations = DietaryRules.Validate(
            ["spaghetti pasta", "tomato sauce"],
            new DietaryProfile { Restrictions = ["gluten-free"] }
        );
        Assert.NotEmpty(violations);
    }

    [Fact]
    public void GlutenFree_RiceAndVegetables_Clean()
    {
        var violations = DietaryRules.Validate(
            ["white rice", "broccoli", "olive oil"],
            new DietaryProfile { Restrictions = ["gluten-free"] }
        );
        Assert.Empty(violations);
    }

    // ── Vegan ─────────────────────────────────────────────────────────

    [Fact]
    public void Vegan_ChickenIngredient_HasViolations()
    {
        var violations = DietaryRules.Validate(
            ["chicken breast", "garlic", "olive oil"],
            new DietaryProfile { Restrictions = ["vegan"] }
        );
        Assert.NotEmpty(violations);
    }

    [Fact]
    public void Vegan_EggsIngredient_HasViolations()
    {
        var violations = DietaryRules.Validate(
            ["eggs", "flour", "sugar"],
            new DietaryProfile { Restrictions = ["vegan"] }
        );
        Assert.NotEmpty(violations);
    }

    [Fact]
    public void Vegan_PurelyPlantBased_Clean()
    {
        var violations = DietaryRules.Validate(
            ["chickpeas", "tahini", "lemon", "garlic"],
            new DietaryProfile { Restrictions = ["vegan"] }
        );
        Assert.Empty(violations);
    }

    // ── Vegetarian ────────────────────────────────────────────────────

    [Fact]
    public void Vegetarian_BeefIngredient_HasViolations()
    {
        var violations = DietaryRules.Validate(
            ["ground beef", "onion", "tomato"],
            new DietaryProfile { Restrictions = ["vegetarian"] }
        );
        Assert.NotEmpty(violations);
    }

    [Fact]
    public void Vegetarian_CheeseAllowed()
    {
        var violations = DietaryRules.Validate(
            ["cheddar cheese", "eggs", "butter"],
            new DietaryProfile { Restrictions = ["vegetarian"] }
        );
        Assert.Empty(violations);
    }

    // ── Empty / No Profile ────────────────────────────────────────────

    [Fact]
    public void EmptyProfile_AlwaysClean()
    {
        var violations = DietaryRules.Validate(
            ["chicken", "cheese", "wheat flour", "eggs"],
            new DietaryProfile()
        );
        Assert.Empty(violations);
    }

    [Fact]
    public void EmptyIngredients_NoViolations()
    {
        var violations = DietaryRules.Validate([], new DietaryProfile { Restrictions = ["vegan"] });
        Assert.Empty(violations);
    }

    // ── CheckAllergy direct ───────────────────────────────────────────

    [Fact]
    public void CheckAllergy_Shellfish_FindsShrimp()
    {
        var violations = DietaryRules.CheckAllergy(["shrimp", "garlic", "butter"], "shellfish");
        Assert.NotEmpty(violations);
    }

    [Fact]
    public void IsKnownRestriction_Vegan_True()
    {
        Assert.True(DietaryRules.IsKnownRestriction("vegan"));
    }

    [Fact]
    public void IsKnownAllergy_Nuts_True()
    {
        Assert.True(DietaryRules.IsKnownAllergy("nuts"));
    }

    [Fact]
    public void IsKnownRestriction_Unknown_False()
    {
        Assert.False(DietaryRules.IsKnownRestriction("breatharian"));
    }
}
