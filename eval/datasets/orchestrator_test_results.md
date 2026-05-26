# ChefAgent — Orchestrator Test Results

**Generated:** 2026-05-26 16:28

| TC | Group | Scenario | Expected Intent | Actual Intent | Classified By | Recipes | Diet | Status |
|----|-------|----------|----------------|---------------|--------------|---------|------|--------|
| 01 | Pure Search | Simple recipe query — no profile | SearchRecipe | SearchRecipe | rules-default | 5 | — | ✅ |
| 02 | Pure Search | Recipe query with action word | SearchRecipe | SearchRecipe | rules-default | 5 | — | ✅ |
| 03 | Pure Search | Vague recipe query | SearchRecipe | SearchRecipe | rules-default | 5 | — | ✅ |
| 04 | Search + Diet | Dietary term in message — no profil | SearchRecipe | Request timed out (6 | — | — | — | ⏱ |
| 05 | Search + Diet | Profile in DTO — no dietary term in | SearchRecipe | SearchRecipe | rules-default | 5 | ✓ | ✅ |
| 06 | Search + Diet | Dietary term in message + profile i | SearchRecipe | SearchRecipe | rules-default | 5 | ✓ | ✅ |
| 07 | Search + Diet | Allergy extracted from message | SearchRecipe | Request timed out (6 | — | — | — | ⏱ |
| 08 | Search + Diet | Indian diet restriction | SearchRecipe | SearchRecipe | rules-default | 5 | ✓ | ✅ |
| 09 | ValidateDiet | Allergy check signal phrase | ValidateDiet | ValidateDiet | rules | 1 | ✓ | ✅ |
| 10 | ValidateDiet | Safety check signal phrase | ValidateDiet | ValidateDiet | rules | 1 | ✓ | ✅ |
| 11 | ValidateDiet | ValidateDiet with no profile — shou | ValidateDiet | ValidateDiet | rules | 0 | — | ✅ |
| 12 | ValidateDiet | Contains check signal phrase | ValidateDiet | ValidateDiet | rules | 0 | — | ✅ |
| 13 | MealPlan | Explicit meal plan request | CreateMealPlan | SearchRecipe | rules-default | 5 | — | ❌ |
| 14 | MealPlan | Multi-intent: search + meal plan | CreateMealPlan | CreateMealPlan | rules | 0 | — | ✅ |
| 15 | GeneralQuestion | Cooking technique question | GeneralQuestion | Request timed out (6 | — | — | — | ⏱ |
| 16 | GeneralQuestion | How-to cooking question | GeneralQuestion | Request timed out (6 | — | — | — | ⏱ |
| 17 | Edge Cases | Out of scope — weather | SearchRecipe | SearchRecipe | rules-default | 5 | — | ✅ |
| 18 | Edge Cases | Ambiguous — no clear intent | SearchRecipe | SearchRecipe | rules-default | 5 | — | ✅ |
| 19 | Edge Cases | Profile only — no message content | SearchRecipe | SearchRecipe | rules-default | 5 | ✓ | ✅ |
| 20 | Edge Cases | Complex multi-constraint query | SearchRecipe | SearchRecipe | rules-default | 5 | ✓ | ✅ |