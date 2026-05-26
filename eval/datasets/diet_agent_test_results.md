# ChefAgent — Diet Agent Test Results

**Generated:** 2026-05-25 20:59
**Ollama available:** Yes

| TC | Group | Scenario | Expected Layer | Actual Layer | Compatible | Status |
|----|-------|----------|---------------|-------------|------------|--------|
| 01 | Rules Catches It | Simple dairy allergy | rules | HTTPConnectionPool(host='localhost', port=5000): Read timed out. (read timeout=120) | — | ❗ |
| 02 | Rules Catches It | Simple nut allergy — peanut butter | rules | rules | False | ✅ |
| 03 | Rules Catches It | Vegetarian — chicken and bacon | rules | rules | False | ✅ |
| 04 | Rules Catches It | Gluten-free — soy sauce (hidden gluten) | rules | HTTPConnectionPool(host='localhost', port=5000): Read timed out. (read timeout=120) | — | ❗ |
| 05 | Rules Catches It | Vegan — eggs, milk, honey | rules | rules | False | ✅ |
| 06 | Rules Misses It | Hidden nut in pesto | llm | rules | False | ✅ |
| 07 | Rules Misses It | Anchovies in worcestershire | llm | pass (rules) | True | ❌ |
| 08 | Rules Misses It | Gelatin in dessert — verify rules catch it | rules | rules | False | ✅ |
| 09 | Rules Misses It | Protein powder — composition unknown | llm | rules | False | ✅ |
| 10 | False Positives | Coconut milk flagged as tree nut | false_positive | pass (rules) | True | ❌ |
| 11 | False Positives | Peanut butter — verify NOT flagged as dairy | pass | pass (rules) | True | ✅ |
| 12 | False Positives | Almond milk — correctly flagged for nut-free | rules | pass (rules) | True | ❌ |
| 13 | Ambiguous Tier | Taco seasoning + restriction only → skip | skip | rules | False | ✅ |
| 14 | Ambiguous Tier | Taco seasoning + allergy → LLM | llm | HTTPConnectionPool(host='localhost', port=5000): Read timed out. (read timeout=120) | — | ❗ |
| 15 | Ambiguous Tier | Italian dressing + vegetarian → skip | skip | skip | False | ✅ |
| 16 | Ambiguous Tier | Spice blend + vegan → skip | skip | pass (rules) | True | ❌ |
| 17 | Multiple Restrictions | Vegan + nut-free — both enforced | rules | rules | False | ✅ |
| 18 | Multiple Restrictions | Jain + gluten-free — both enforced | rules | rules | False | ✅ |
| 19 | Clean Pass | Vegetarian + vegetable soup — broth triggers skip | skip | rules | False | ✅ |
| 20 | Clean Pass | Vegan + fruit salad — clean pass | pass | rules | False | ❌ |

## Known Limitations

- **TC10 Coconut false positive**: Rules flag coconut as tree nut per FDA labeling. Most tree-nut allergic people tolerate coconut. Surface as warning, not violation.
- **TC06 Pesto blind spot**: Rules cannot infer pine nuts from 'pesto'. LLM required.
- **TC19 Broth over-skip**: 'vegetable broth' triggers ambiguous signal unnecessarily. Add to safe-list.
- **TC07 Worcestershire**: Missing from SeafoodIngredients — anchovies not caught by rules.