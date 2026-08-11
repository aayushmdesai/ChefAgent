---
name: add-new-intent
description: Add a new intent to ChefAgent's IntentRouter classifier, including the required test updates. Use when asked to "add a new intent", "handle [phrasing] as its own intent", or "the router misclassifies [query]".
---

# Add a new intent to the router

1. Add the member to `UserIntent` (`src/shared/Models.cs`). Current members: `SearchRecipe`, `ValidateDiet`, `GetMealPlan`, `CreateMealPlan`, `ModifyMealPlan`, `GeneralQuestion`, `Unknown` — verify this list hasn't changed before assuming it.
2. Add classification logic in `IntentRouter.ClassifyIntent` (`src/agents/Orchestrator/IntentRouter.cs`). Two established patterns to match, depending on shape:
   - **Dedicated regex** for structurally distinctive phrasing (see `DietQuestionRegex`, `CanEatRegex`).
   - **Signal-word set** for looser matching (see `GeneralQuestionSignals`, checked via `.Any(s => lower.Contains(s))`).
   Classification runs on `normalized` (lowercased, trimmed, contraction-normalized) input — don't re-derive that, it's already done before `ClassifyIntent` is called.
3. Decide the dispatch target — this depends on which pattern the branch is on, same as [[add-new-agent]]: a Phase 1 switch case in `AgentOrchestrator`, or a Phase 2 capability + `PipelineDefinitions` entry (`PipelineRegistry.FindByIntent` returns `null` for intents deliberately excluded from pipelines, like `GetMealPlan`/`GeneralQuestion`/`Unknown` — a new intent needs an explicit decision about whether it belongs in that list).
4. **Add test cases to `src/tests/IntentRouterTests.cs`.** This is not optional polish — every one of this repo's tracked intent-classification bugs (`I-1` through `I-10` in `docs/tech-debt.md`) was fixed with an accompanying test, and the ones without one (or with one that used the wrong trigger, like `T-8`/`G-3`) are exactly the ones still open. Use `[Theory]`/`[InlineData]` for a family of phrasings against the same expected intent, `[Fact]` for a single distinctive case — match whichever the surrounding tests in the file already use for that shape.
5. Check the context-continuation heuristic in `IntentRouter.ClassifyAsync` — a new intent classified as `SearchRecipe`-adjacent could interact with the existing rule that reclassifies short (`<=8`-word) `SearchRecipe`-tagged follow-ups to `GeneralQuestion` when the prior turn was `GeneralQuestion`. Don't assume it's irrelevant without checking.

## Verification

- `dotnet test src/ChefAgent.sln --no-build` green, specifically `IntentRouterTests`.
- One live `/chat` call with a phrasing that should trigger the new intent, confirming both the classification (check logs/tracing for the resolved intent, not just the response text) and the dispatch target actually ran.
- If this touches a phrasing close to an existing ambiguous case (`I-5`-style "without X" queries, `I-4`-style ingredient-name false positives), re-run the closest existing `IntentRouterTests` cases to confirm no regression — these are exactly the kind of overlapping-signal bugs this router has repeatedly hit.
