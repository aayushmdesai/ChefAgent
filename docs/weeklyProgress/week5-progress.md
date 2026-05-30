# ChefAgent — Week 5 Progress Log

**Date:** May 26, 2026
**Goal:** Planner Agent — stateful meal plan generation, Redis session memory, modify/refine flow
**Status:** ✅ Day 1 (Schema + MealPlannerPlugin) Complete | ✅ Day 2 (Redis SessionStore) Complete | ✅ Day 3 (ModifyPlanAsync) Complete | ✅ Day 4 (Variety Refinement) Complete | ✅ Day 5 (Test Matrix) Complete | ✅ Days 6–7 (Orchestrator + UI + ADR) Complete

---

## What We're Building

The first stateful agent in the system. Every agent so far (Recipe, Diet, Orchestrator) is memoryless — each request is independent. The Planner is fundamentally different: it generates a plan, persists it, and allows the user to iteratively modify it across multiple turns.

```
Weeks 1–4:  Stateless agents — every request independent

Week 5:     CreateMealPlan  → PlannerPlugin → Recipe ×7 + Diet ×7 → Redis → Response
            ModifyMealPlan  → SessionStore.GetPlan → PlannerPlugin.Modify → Redis → Response
```

---

## Conceptual Foundation — Why Stateful Is Harder

The Recipe Agent answers a question. The Diet Agent checks a constraint. The Planner _maintains a thing_ over time — a 7-day plan that the user can inspect and iteratively refine. That state has to live somewhere between requests.

Three new problems this introduces that didn't exist in Weeks 1–4:

1. **Where does state live?** In-memory dies on restart. Redis survives restarts and scales to multiple server instances.
2. **How do you scope state to a user?** No auth in MVP — `sessionId` (a client-generated GUID) is the user identity. The client holds it; Redis keys on it.
3. **What happens to stale state?** TTL (7 days) on every Redis key — plans expire automatically, no cleanup job needed.

---

## Day 1 — Schema and Plugin Generation Logic

### Schema additions (`Models.cs`)

Four new records added to capture what the Planner needs to reason about:

```csharp
public record MealSlot
{
    public required string SlotName { get; init; }        // "breakfast", "lunch", "dinner"
    public required RecipeDocument Recipe { get; init; }
    public DietaryValidation? DietaryValidation { get; init; }
    public string? ProteinCategory { get; init; }         // "poultry", "beef", "fish", "vegetarian"
    public string? CuisineTag { get; init; }              // "italian", "mexican", "asian", "american"
}

public record DayPlan
{
    public required string Day { get; init; }             // "Monday" … "Sunday"
    public required List<MealSlot> Slots { get; init; }
}

public record MealPlan
{
    public required string PlanId { get; init; }
    public required List<DayPlan> Days { get; init; }
    public PlanConstraints? Constraints { get; init; }
    public DietaryProfile? Profile { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public string Status { get; init; } = "draft";
}

public record PlanConstraints
{
    public List<string> MealSlots { get; init; } = ["dinner"];
    public int? MaxStepsWeeknight { get; init; }
    public int? MaxStepsWeekend { get; init; }
    public int HouseholdSize { get; init; } = 2;
    public bool AvoidProteinRepeat { get; init; } = true;
    public bool AvoidCuisineRepeat { get; init; } = true;
}
```

**Why `ProteinCategory` and `CuisineTag` on `MealSlot`?**
Variety enforcement needs to look back at what was planned on previous days. Storing the inferred category on the slot means `SelectWithVariety` doesn't have to re-infer it on every comparison — computed once at generation time, reused on every modify.

**Why `Status = "draft"`?**
Allows the user to iterate before committing. "Finalized" as a future state enables Month 2 features like shopping list generation — you only generate a list from a finalized plan, not a draft being actively modified.

### `MealPlannerPlugin.cs` — new file

Created `src/agents/PlannerAgent/MealPlannerPlugin.cs`.

**Namespace issue encountered and resolved:** `Recipe` is a namespace in this project (`ChefAgent.Agents.Recipe`), not a type. Adding `using ChefAgent.Agents.Recipe` caused a collision — `Recipe` became ambiguous between the namespace and any type named `Recipe`. Fix: removed the using, referenced `RecipeSearchPlugin` via its full namespace in the constructor and field declaration. The actual model type is `RecipeDocument` (in `ChefAgent.Shared.Models`).

#### Generation flow

```
GeneratePlanAsync(profile, constraints)
    │
    ├─ foreach day in [Monday … Sunday]
    │       foreach slotName in constraints.MealSlots
    │           GenerateSlotAsync(day, slotName, profile, constraints, plannedSoFar)
    │               ├─ Pick query: SlotQueries[slotName][dayIndex % queryCount]
    │               ├─ SearchRecipesAsync(query, maxResults: 5, maxSteps)
    │               ├─ SelectWithVariety(candidates, plannedSoFar, constraints)
    │               └─ ValidateRecipeAsync(recipe, profile)  ← only if profile != null
    │
    └─ return MealPlan { PlanId, Days, Constraints, Profile }
```

#### Query rotation

Each slot has 7 queries (one per day of the week), rotated by day index:

```csharp
var query = queries[dayIndex % queries.Length];
```

This avoids sending the identical query to Qdrant 7 times, which would return near-identical top results every day. Different queries surface different parts of the recipe space.

#### Variety enforcement (`SelectWithVariety`)

Looks back at the last 2 planned days, collects their protein categories and cuisine tags, and skips any candidate that repeats either. Falls back to the top candidate if no variety winner is found (e.g. a very restrictive diet with limited recipe variety).

```csharp
var recentProteins = plannedSoFar.TakeLast(2)
    .SelectMany(d => d.Slots)
    .Select(s => s.ProteinCategory)
    .Where(p => p is not null)
    .ToHashSet();
```

#### Protein and cuisine inference

Keyword matching on `recipe.Title + recipe.Ingredients` — no LLM needed. Covers the common cases; `null` is an acceptable result for recipes that don't match any keyword (not every recipe has a classifiable protein or cuisine).

#### Weeknight vs weekend complexity

```csharp
var maxSteps = isWeeknight ? constraints.MaxStepsWeeknight : constraints.MaxStepsWeekend;
```

If `MaxStepsWeeknight` is set (e.g. 5), weeknight searches pass `maxSteps: 5` to `RecipeSearchPlugin` — reusing the payload filtering built in Week 2. Weekend slots get the higher cap or no cap at all.

### Service registration (`ServiceRegistration.cs`)

Added `AddMealPlannerAgent` method:

```csharp
private static IServiceCollection AddMealPlannerAgent(
    this IServiceCollection services,
    IConfiguration config)
{
    services.AddSingleton(sp => new MealPlannerPlugin(
        sp.GetRequiredService<ChefAgent.Agents.Recipe.RecipeSearchPlugin>(),
        sp.GetRequiredService<DietValidationPlugin>(),
        sp.GetRequiredService<ILogger<MealPlannerPlugin>>()
    ));
    return services;
}
```

Called in `AddChefAgentServices` after `AddDietAgent`, before `AddOrchestrator`.

### Smoke test results

```
POST /debug/plan  (endpoint removed after validation)
Response time: ~17 seconds (7 × vector search on CPU)
```

| Day       | Recipe                           | Protein | Cuisine    |
| --------- | -------------------------------- | ------- | ---------- |
| Monday    | Sunday Dinner                    | poultry | null       |
| Tuesday   | Beef And Macaroni Skillet Dinner | beef    | null       |
| Wednesday | "Delightfully Quick Stir-Fry"    | null    | italian ⚠️ |
| Thursday  | Vegetable Soup                   | null    | null       |
| Friday    | Baked Fish                       | fish    | null       |
| Saturday  | Tenderloin Deluxe                | pork    | asian      |
| Sunday    | Red Beans And Rice               | beef    | null       |

**Assessment:**

- ✅ 7 days, 7 real recipes — generation works end to end
- ✅ No consecutive protein repeats on matched categories
- ✅ Variety enforcement fired — beef on Tuesday did not repeat until Sunday
- ⚠️ Wednesday: "Stir-Fry" tagged as `italian` — false cuisine tag, likely a keyword collision in `CuisineKeywords`. Logged as known limitation, fix in Day 4 variety refinement pass
- ℹ️ `cuisine: null` on most days is expected — 10K recipe dataset is American-heavy with generic titles

### Known limitation noted

`cuisine: null` is not a bug — it means the recipe didn't match any cuisine keyword. The variety enforcement still works correctly for the `null` case (two consecutive `null` cuisines are allowed, since "unclassified" is not a cuisine category to avoid repeating).

---

## Day 2 — Redis Session Memory (`SessionStore.cs`)

### What it does

A thin wrapper around Redis that persists meal plans and dietary profiles across requests, keyed by `sessionId`. No auth, no user table — `sessionId` is the user identity in MVP.

### Key schema

```
session:{sessionId}:plan     → JSON-serialized MealPlan     TTL 7 days
session:{sessionId}:profile  → JSON-serialized DietaryProfile  TTL 7 days
```

**Why separate keys for plan and profile?** Profile changes more often than the plan. Storing them together means every profile update requires deserializing and re-serializing the entire plan. Separate keys — separate concerns.

**Why 7-day TTL?** A user might generate a plan Sunday night and come back Monday morning. 24 hours is too short. 7 days covers a full meal plan cycle. Plans expire automatically — no cleanup job needed.

### `SessionStore.cs`

```csharp
public class SessionStore
{
    private readonly IDatabase _db;
    private static readonly TimeSpan DefaultTTL = TimeSpan.FromDays(7);

    public async Task SavePlanAsync(string sessionId, MealPlan plan) { ... }
    public async Task<MealPlan?> GetPlanAsync(string sessionId) { ... }
    public async Task DeletePlanAsync(string sessionId) { ... }

    // Profile methods — stubbed, wired in Month 2
    public async Task SaveProfileAsync(string sessionId, DietaryProfile profile) { ... }
    public async Task<DietaryProfile?> GetProfileAsync(string sessionId) { ... }
}
```

`GetPlanAsync` returns `null` when the key doesn't exist or has expired — callers handle the null case gracefully rather than throwing.

### Service registration

Added `AddRedis` method to `ServiceRegistration.cs`:

```csharp
private static IServiceCollection AddRedis(
    this IServiceCollection services,
    IConfiguration config)
{
    var connectionString = config["Redis:ConnectionString"] ?? "localhost:6379";
    services.AddSingleton<IConnectionMultiplexer>(
        ConnectionMultiplexer.Connect(connectionString));
    services.AddSingleton<SessionStore>();
    return services;
}
```

Called first in `AddChefAgentServices` — before all agents, since agents may eventually depend on session state.

### Smoke test results

Three-way verification — API round-trip + direct Redis inspection:

```bash
# 1. Generate and persist
POST /debug/plan-persist
→ { "sessionId": "test-session-001", "planId": "f8fa995f-...", "days": 7 }
Response time: ~17s (7 × vector search)

# 2. Retrieve from Redis via API
GET /debug/plan-persist/test-session-001
→ { "planId": "f8fa995f-...", "days": 7, "status": "draft" }
Response time: ~1ms (Redis read)

# 3. Verify directly in Redis
docker compose exec redis redis-cli GET session:test-session-001:plan
→ "f8fa995f-..." / "draft" / 7 days confirmed
```

**Key observation:** plan generation is slow once (17s — 7 vector searches), every subsequent read is instant (1ms — Redis). This is the performance profile: generate once, read many times. The modify flow will follow the same pattern — load from Redis (instant), one new vector search for the replacement slot, save back (instant).

Both debug endpoints removed after validation.

---

## Files Created / Changed

```
src/shared/Models.cs                          # Updated: MealSlot, DayPlan, MealPlan, PlanConstraints records added
src/shared/SessionStore.cs                    # New: Redis-backed plan + profile persistence, TTL 7 days
src/agents/PlannerAgent/MealPlannerPlugin.cs  # New: generation logic, variety enforcement, protein/cuisine inference
src/api/ServiceRegistration.cs                # Updated: AddMealPlannerAgent + AddRedis + call order in AddChefAgentServices
appsettings.json                              # Updated: Redis:ConnectionString added
```

---

## Concepts Learned

| Concept                         | What It Means                                                                                                                                                                                                                |
| ------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Stateful vs stateless agents    | Stateless agents answer questions; stateful agents maintain things over time. The Planner is the first agent that _owns_ something between requests.                                                                         |
| Namespace collision             | `Recipe` was both a namespace and an intended type name — importing the namespace made the type reference ambiguous. Fix: use full namespace qualification on the field/constructor, don't import the conflicting namespace. |
| Query rotation for diversity    | Sending different queries per day surfaces different parts of the vector space. Same query 7 times = near-identical results 7 times.                                                                                         |
| Variety as a post-search filter | Protein/cuisine variety is enforced _after_ retrieval, not during. Retrieval finds the best semantic match; variety selection picks the best match that doesn't repeat recent categories.                                    |
| Pre-computed slot metadata      | `ProteinCategory` and `CuisineTag` stored on `MealSlot` at generation time — avoid re-inferring on every subsequent comparison during modify flows.                                                                          |
| Separate Redis keys per concern | Plan and profile stored under different keys — profile changes don't require re-serializing the full plan.                                                                                                                   |
| Null-returning reads            | `GetPlanAsync` returns `null` on missing/expired keys rather than throwing — callers handle the absence gracefully.                                                                                                          |
| Generate-once, read-many        | Plan generation is expensive (17s, 7 vector searches). Redis makes every subsequent read instant (1ms). State persistence changes the cost model entirely.                                                                   |

---

## What's Next (Days 3–7)

---

## Day 3 — Modify Flow (`ModifyPlanAsync`)

### What it does

Loads an existing plan from Redis, swaps a single slot, validates against the stored profile, saves back. The user sees only the changed day — the rest of the plan is untouched.

```
ModifyPlanAsync(sessionId, targetDay, targetSlot, constraint)
    │
    ├─ SessionStore.GetPlanAsync()         ← instant (Redis)
    ├─ BuildModifyQuery(constraint)        ← "pasta" → "pasta dinner"
    ├─ SearchRecipesAsync(query)           ← one vector search (~2s)
    ├─ SelectWithVarietyForModify()        ← checks both neighbors, not just lookback
    ├─ ValidateRecipeAsync()               ← only if plan.Profile != null
    ├─ Immutable update (Select + with)    ← rebuild day, pass everything else through
    ├─ SessionStore.SavePlanAsync()        ← instant (Redis)
    └─ return (updatedPlan, message)
```

### Immutable record update

Records are `init`-only — you can't mutate them in place. The update uses `Select` + `with` to rebuild only what changed:

```csharp
var updatedDays = plan.Days.Select(d =>
    d.Day != targetDay ? d :
    d with
    {
        Slots = d.Slots.Select(s =>
            s.SlotName != targetSlot ? s : newSlot
        ).ToList()
    }
).ToList();
```

Everything that doesn't match `targetDay` passes through unchanged. Everything inside the matching day that doesn't match `targetSlot` passes through unchanged. Only the one target slot is replaced.

### Generation vs modify variety logic

Two separate methods — `SelectWithVariety` (generation) and `SelectWithVarietyForModify` (modify) — because the available context is different:

|                   | Generation                          | Modify                           |
| ----------------- | ----------------------------------- | -------------------------------- |
| Context available | Days planned so far (lookback only) | Full plan (look both directions) |
| Neighbor check    | Last 2 days                         | Closest 2 days by index distance |

During generation, future days don't exist yet — you can only look back. During modify, the full plan exists — checking both the previous and next day gives better variety enforcement.

### Constraint parsing (`BuildModifyQuery`)

Simple string replacement — no LLM needed for common patterns:

| User says                | Query sent to Qdrant                    |
| ------------------------ | --------------------------------------- |
| `null`                   | Default slot query ("weeknight dinner") |
| `"something with pasta"` | `"pasta dinner"`                        |
| `"something simpler"`    | `"simple quick dinner"`                 |
| `"something Italian"`    | `"Italian dinner"`                      |

### Smoke test results

```bash
# Tuesday swap — no constraint
POST /debug/plan-modify/test-session-001/Tuesday
→ { "message": "Swapped Tuesday's dinner to Sunday Dinner.", "protein": "poultry" }
Response time: ~2s (Redis load + 1 vector search + Redis save)

# Wednesday swap — pasta constraint
POST /debug/plan-modify/test-session-001/Wednesday?constraint=pasta
→ { "message": "Swapped Wednesday's dinner to Pasta Salad (pasta).", "cuisine": "italian" }
Response time: ~1s
```

**Key observation:** modify takes 1–2s vs 19s for full generation. The performance profile holds — Redis load is instant, one vector search replaces seven.

**Variety fallback noted:** Tuesday returned "Sunday Dinner" (poultry) — same protein as Monday. The variety check fired but fell back because all 5 candidates repeated neighbors. Expected behavior with a 10K dataset where the same top candidates recur for similar queries. Logged as known limitation.

### Debug endpoints

Three temporary debug endpoints (`/debug/plan-persist`, `/debug/plan-persist/{sessionId}`, `/debug/plan-modify/{sessionId}/{day}`) were added for smoke testing and removed from `Endpoints.cs` after validation. Not part of the production API.

---

## Files Created / Changed

```
src/shared/Models.cs                          # Updated: MealSlot, DayPlan, MealPlan, PlanConstraints records added
src/shared/SessionStore.cs                    # New: Redis-backed plan + profile persistence, TTL 7 days
src/agents/PlannerAgent/MealPlannerPlugin.cs  # New: GeneratePlanAsync, ModifyPlanAsync, variety enforcement
src/api/ServiceRegistration.cs                # Updated: AddMealPlannerAgent + AddRedis + call order
src/api/Endpoints.cs                          # Updated: debug endpoints added then removed after validation
appsettings.json                              # Updated: Redis:ConnectionString added
```

---

## Concepts Learned

| Concept                         | What It Means                                                                                                                                                                                                                |
| ------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Stateful vs stateless agents    | Stateless agents answer questions; stateful agents maintain things over time. The Planner is the first agent that _owns_ something between requests.                                                                         |
| Namespace collision             | `Recipe` was both a namespace and an intended type name — importing the namespace made the type reference ambiguous. Fix: use full namespace qualification on the field/constructor, don't import the conflicting namespace. |
| Query rotation for diversity    | Sending different queries per day surfaces different parts of the vector space. Same query 7 times = near-identical results 7 times.                                                                                         |
| Variety as a post-search filter | Protein/cuisine variety is enforced _after_ retrieval, not during. Retrieval finds the best semantic match; variety selection picks the best match that doesn't repeat recent categories.                                    |
| Pre-computed slot metadata      | `ProteinCategory` and `CuisineTag` stored on `MealSlot` at generation time — avoid re-inferring on every subsequent comparison during modify flows.                                                                          |
| Separate Redis keys per concern | Plan and profile stored under different keys — profile changes don't require re-serializing the full plan.                                                                                                                   |
| Null-returning reads            | `GetPlanAsync` returns `null` on missing/expired keys rather than throwing — callers handle the absence gracefully.                                                                                                          |
| Generate-once, read-many        | Plan generation is expensive (19s, 7 vector searches). Redis makes every subsequent read instant (1ms). State persistence changes the cost model entirely.                                                                   |
| Immutable record update         | C# `init`-only records can't be mutated — use `Select` + `with` to rebuild only the changed node; everything else passes through unchanged.                                                                                  |
| Generation vs modify variety    | Generation looks back (future days don't exist yet). Modify checks both neighbors (full plan available). Same principle, different context window.                                                                           |
| Debug endpoints as scaffolding  | Temporary endpoints for smoke testing are added and removed — they're not production API surface. Document what they tested, not that they existed.                                                                          |

---

## What's Next (Days 4–7)

---

## Day 4 — Variety Refinement (False Cuisine Tag Fix)

### Problem

From Day 1 smoke test: "Delightfully Quick Stir-Fry" was tagged `cuisine: italian`. The stir-fry recipe contained `"1 (14 oz.) can Contadina pasta ready"` as an ingredient — the word `"pasta"` matched the italian keyword list, producing a false cuisine tag.

Root cause: keyword matching on ingredients is too broad. `"pasta"` as a canned sauce ingredient doesn't mean the dish is Italian cuisine. Same issue with `"soy sauce"` appearing in American fusion recipes and triggering the asian tag.

### Why not match on title only?

Title-only matching would eliminate false positives but miss recipes with cuisine-specific ingredients and generic titles ("Mom's Casserole" with Italian herbs). More importantly, the project will eventually include Gujarati and Hindi recipes where cuisine signal lives in ingredient names (`dhokla`, `thepla`, `masala`), not English-language titles. Title-only matching doesn't scale to multilingual datasets.

### Fix — `CuisineFalsePositives` safe-list

Same pattern as `DairyFalsePositives` in the Diet Agent (Week 3) — check known false positive phrases before running the main keyword scan:

```csharp
private static readonly HashSet<string> CuisineFalsePositives =
[
    "pasta ready",       // "Contadina pasta ready" — canned sauce, not Italian dish
    "pasta sauce",       // same
    "italian dressing",  // salad dressing, not Italian cuisine
    "italian mix",       // seasoning packet
    "soy sauce",         // appears in American/fusion recipes, not just Asian
];

private static string? InferCuisineTag(RecipeDocument recipe)
{
    var ingredients = string.Join(" ", recipe.Ingredients ?? []).ToLowerInvariant();
    var title = recipe.Title.ToLowerInvariant();

    // Check false positives on ingredients first
    if (CuisineFalsePositives.Any(fp => ingredients.Contains(fp)))
        return null;

    var text = $"{title} {ingredients}";
    foreach (var (tag, keywords) in CuisineKeywords)
        if (keywords.Any(k => text.Contains(k)))
            return tag;

    return null;
}
```

### Extended `CuisineKeywords` for future languages

```csharp
["indian"]   = ["masala", "tikka", "biryani", "dal", "paneer", "naan", "chutney", "samosa"],
["gujarati"] = ["dhokla", "thepla", "undhiyu", "kadhi", "fafda"],
["punjabi"]  = ["butter chicken", "sarson da saag", "makki di roti", "lassi"],
```

Keywords for Gujarati and Punjabi cuisine added now — commented out in production until recipes exist in the dataset. The structure is ready; the data isn't yet.

### Verification

Diagnosed with a temporary `Console.WriteLine` in `InferCuisineTag`, confirmed via server logs after flushing Redis (`FLUSHDB`) and regenerating:

```
[CuisineTag] False positive 'pasta ready' matched — skipping tag for '"Delightfully Quick Stir-Fry"'
[CuisineTag] False positive 'soy sauce' matched — skipping tag for 'Tenderloin Deluxe'
```

Both false positives fire on every generation run. `Console.WriteLine` removed after confirmation.

**Why the log fires twice per recipe:** `InferCuisineTag` is called during `SelectWithVariety` (checking all 5 candidates) and again when the winner is stored on the `MealSlot`. Two calls, not a bug.

### Stale Redis — important debugging note

The fix appeared not to work initially because Redis had the old plan cached with the wrong cuisine tag. `FLUSHDB` cleared it and the fresh generation showed correct results. **When debugging inference logic changes, always flush Redis first** — otherwise you're reading cached pre-fix data.

---

## Files Created / Changed

```
src/shared/Models.cs                          # Updated: MealSlot, DayPlan, MealPlan, PlanConstraints records added
src/shared/SessionStore.cs                    # New: Redis-backed plan + profile persistence, TTL 7 days
src/agents/PlannerAgent/MealPlannerPlugin.cs  # New: GeneratePlanAsync, ModifyPlanAsync, variety enforcement, false positive fix
src/api/ServiceRegistration.cs                # Updated: AddMealPlannerAgent + AddRedis + call order
src/api/Endpoints.cs                          # Updated: debug endpoints added then removed after validation
appsettings.json                              # Updated: Redis:ConnectionString added
```

---

## Concepts Learned

| Concept                         | What It Means                                                                                                                                                                                                                |
| ------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Stateful vs stateless agents    | Stateless agents answer questions; stateful agents maintain things over time. The Planner is the first agent that _owns_ something between requests.                                                                         |
| Namespace collision             | `Recipe` was both a namespace and an intended type name — importing the namespace made the type reference ambiguous. Fix: use full namespace qualification on the field/constructor, don't import the conflicting namespace. |
| Query rotation for diversity    | Sending different queries per day surfaces different parts of the vector space. Same query 7 times = near-identical results 7 times.                                                                                         |
| Variety as a post-search filter | Protein/cuisine variety is enforced _after_ retrieval, not during. Retrieval finds the best semantic match; variety selection picks the best match that doesn't repeat recent categories.                                    |
| Pre-computed slot metadata      | `ProteinCategory` and `CuisineTag` stored on `MealSlot` at generation time — avoid re-inferring on every subsequent comparison during modify flows.                                                                          |
| Separate Redis keys per concern | Plan and profile stored under different keys — profile changes don't require re-serializing the full plan.                                                                                                                   |
| Null-returning reads            | `GetPlanAsync` returns `null` on missing/expired keys rather than throwing — callers handle the absence gracefully.                                                                                                          |
| Generate-once, read-many        | Plan generation is expensive (19s, 7 vector searches). Redis makes every subsequent read instant (1ms). State persistence changes the cost model entirely.                                                                   |
| Immutable record update         | C# `init`-only records can't be mutated — use `Select` + `with` to rebuild only the changed node; everything else passes through unchanged.                                                                                  |
| Generation vs modify variety    | Generation looks back (future days don't exist yet). Modify checks both neighbors (full plan available). Same principle, different context window.                                                                           |
| Safe-list pattern (reused)      | `CuisineFalsePositives` applies the same pattern as `DairyFalsePositives` from Week 3 — check known false positives before running the main rule scan. Consistent across agents.                                             |
| Stale cache masking fixes       | Always flush Redis when debugging inference logic changes — cached data from before the fix will make the fix appear broken.                                                                                                 |
| Debug endpoints as scaffolding  | Temporary endpoints for smoke testing are added and removed — they're not production API surface. Document what they tested, not that they existed.                                                                          |

---

## What's Next (Days 5–7)

---

## Day 5 — Test Matrix

### Setup

15 test cases across 5 groups. Script: `scripts/eval/test_planner.py`. Session: `planner-test-001`.

### Results

| TC  | Scenario                                     | Status         |
| --- | -------------------------------------------- | -------------- |
| 01  | Basic plan generation (days=7)               | ✅             |
| 02  | No consecutive protein repeats on fresh plan | ✅             |
| 03  | No false italian tag on stir-fry             | ✅             |
| 04  | Plan persists across requests (same planId)  | ✅             |
| 05  | Unknown sessionId returns 404                | ✅             |
| 06  | Swap Tuesday — new recipe + message returned | ✅             |
| 07  | Only Tuesday changes on swap                 | ✅             |
| 08  | Swap Wednesday with pasta constraint         | ✅             |
| 09  | Swap Thursday with simpler constraint        | ✅             |
| 10  | Three consecutive swaps all succeed          | ✅             |
| 11  | Invalid day name returns 400                 | ✅             |
| 12  | Modify on missing session returns 404        | ✅             |
| 13  | No protein repeats after adversarial swaps   | ⚠️ test design |
| 14  | No consecutive cuisine repeats               | ✅             |
| 15  | Generation latency < 30s                     | ✅ 14.2s       |

**14/15 passed. 0 system bugs. 1 test design issue.**

### TC13 — test design issue, not a bug

After 5 adversarial swaps (TC06–TC10), the protein sequence became `['poultry', 'poultry', None, 'poultry', 'poultry', 'poultry', 'beef']`. The variety check fired correctly on each swap but fell back because all 5 candidates from the 10K dataset were poultry — the dataset is poultry-heavy for dinner queries.

TC02 tests the same property on a fresh generation and passes with 0 repeats. TC13 tests adversarial repeated swaps which don't reflect real usage. Same failure category as Week 3 diet test matrix — test scenario wrong, system correct.

### Performance note

14.2s for a 7-dinner plan on CPU — meaningfully faster than the 17–19s from Days 1–2. Warmed Ollama (model already loaded in memory) accounts for the difference. Cold start on first request of a session will be slower.

### Key interview talking points

**"14/15 with the 1 failure being a test design issue."** TC13 tested adversarial repeated swaps — not a real usage pattern. TC02, which tests variety on fresh generation, passed cleanly.

**"The test suite caught zero system bugs because the real bugs were caught during development."** The false cuisine tag (Day 4) and the session ID mismatch (Day 5 setup) were both caught before the matrix ran. Test matrices confirm correctness; bugs are caught earlier by smoke testing.

**"14.2s generation on CPU under the 30s target."** The architecture was designed around this constraint from Week 2 onward — opt-in LLM, fast rules path, Redis for reads. The performance profile is a direct result of those decisions.

---

## Files Created / Changed

```
src/shared/Models.cs                            # Updated: MealSlot, DayPlan, MealPlan, PlanConstraints
src/shared/SessionStore.cs                      # New: Redis-backed plan + profile persistence, TTL 7 days
src/agents/PlannerAgent/MealPlannerPlugin.cs    # New: GeneratePlanAsync, ModifyPlanAsync, variety enforcement, false positive fix
src/api/ServiceRegistration.cs                  # Updated: AddMealPlannerAgent + AddRedis + call order
src/api/Endpoints.cs                            # Updated: debug endpoints added (temp) + removed after testing
appsettings.json                                # Updated: Redis:ConnectionString added
scripts/eval/test_planner.py                    # New: 15-case automated test runner
eval/datasets/planner_test_results.md           # New: test matrix with annotated results
```

---

## Concepts Learned

| Concept                         | What It Means                                                                                                                                                                                              |
| ------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Stateful vs stateless agents    | Stateless agents answer questions; stateful agents maintain things over time. The Planner is the first agent that _owns_ something between requests.                                                       |
| Namespace collision             | `Recipe` was both a namespace and an intended type name — importing the namespace made the type reference ambiguous. Fix: use full namespace qualification, don't import the conflicting namespace.        |
| Query rotation for diversity    | Sending different queries per day surfaces different parts of the vector space. Same query 7 times = near-identical results 7 times.                                                                       |
| Variety as a post-search filter | Protein/cuisine variety is enforced _after_ retrieval, not during. Retrieval finds the best semantic match; variety selection picks the best match that doesn't repeat recent categories.                  |
| Pre-computed slot metadata      | `ProteinCategory` and `CuisineTag` stored on `MealSlot` at generation time — avoid re-inferring on every subsequent comparison during modify flows.                                                        |
| Separate Redis keys per concern | Plan and profile stored under different keys — profile changes don't require re-serializing the full plan.                                                                                                 |
| Null-returning reads            | `GetPlanAsync` returns `null` on missing/expired keys rather than throwing — callers handle the absence gracefully.                                                                                        |
| Generate-once, read-many        | Plan generation is expensive (14–19s, 7 vector searches). Redis makes every subsequent read instant (1ms). State persistence changes the cost model entirely.                                              |
| Immutable record update         | C# `init`-only records can't be mutated — use `Select` + `with` to rebuild only the changed node; everything else passes through unchanged.                                                                |
| Generation vs modify variety    | Generation looks back (future days don't exist yet). Modify checks both neighbors (full plan available). Same principle, different context window.                                                         |
| Safe-list pattern (reused)      | `CuisineFalsePositives` applies the same pattern as `DairyFalsePositives` from Week 3 — check known false positives before running the main rule scan. Consistent across agents.                           |
| Stale cache masking fixes       | Always flush Redis when debugging inference logic changes — cached data from before the fix will make the fix appear broken.                                                                               |
| Test design vs system bugs      | TC13 failed because the test scenario was adversarial (5 swaps exhausting variety candidates), not because the system was wrong. Distinguishing "the system is wrong" from "the test is wrong" is a skill. |
| Debug endpoints as scaffolding  | Temporary endpoints for smoke testing are added and removed — not production API surface.                                                                                                                  |

---

## What's Next (Days 6–7)

- [x] ~~Day 2: Redis session memory~~ — Done
- [x] ~~Day 3: ModifyPlanAsync~~ — Done
- [x] ~~Day 4: Variety refinement~~ — Done
- [x] ~~Day 5: Test matrix~~ — Done (14/15, 0 system bugs)
- [x] ~~Day 6–7: Orchestrator wiring + UI + ADR~~ — Done

---

## Day 6–7 — Orchestrator Wiring, React UI, Multi-Slot Planning

### Orchestrator wiring (`AgentOrchestrator.cs`)

Replaced the `CreateMealPlan` and `ModifyMealPlan` `HandlePlaceholder` stubs with real handlers.

**`HandleCreateMealPlanAsync`:**

- Validates `sessionId` is present (required to persist plan)
- Calls `MealPlannerPlugin.GeneratePlanAsync` with extracted `MealSlots` and `MergedProfile`
- Saves plan to Redis via `SessionStore.SavePlanAsync` — critical missing step that caused the first "plan not found" bug
- Builds dynamic response message: "7-day dinner plan" vs "7-day full day (breakfast, lunch, and dinner) plan"

**`HandleModifyMealPlanAsync`:**

- Validates `sessionId` and `TargetDay` (asks clarifying question if day is missing)
- Calls `MealPlannerPlugin.ModifyPlanAsync` with `TargetSlot` (not hardcoded "dinner")
- Returns updated plan in response — UI re-renders the meal plan grid

**`SessionStore` injected into `AgentOrchestrator`** — needed to call `SavePlanAsync` directly after generation. `MealPlannerPlugin` handles saves on modify internally.

### Intent extraction improvements (`IntentRouter.cs`)

**Multi-slot extraction (`ExtractMealSlots`):**

```csharp
private static List<string> ExtractMealSlots(string lower)
{
    var slots = new List<string>();
    if (lower.Contains("breakfast")) slots.Add("breakfast");
    if (lower.Contains("lunch") || lower.Contains("lunches")) slots.Add("lunch");
    if (lower.Contains("dinner") || lower.Contains("dinners")) slots.Add("dinner");
    // No slot words = user said "meals" or "week" = all three
    if (slots.Count == 0) slots = ["breakfast", "lunch", "dinner"];
    return slots;
}
```

**TargetSlot extraction for modify:** Parses "swap Monday breakfast" → `targetDay=Monday`, `targetSlot=breakfast`. Previously always defaulted to "dinner" even on multi-slot plans.

**Additional `MealPlanSignals`:** Added "plan my breakfast and lunch", "plan my lunch and dinner", "plan my breakfast and dinner" and combo variants. Fixed "plan my breakfast and lunch for a week" which was incorrectly classified as `SearchRecipe`.

**Query cleaning fix:** `.Replace(" dinner", " ")` was matching mid-word in `"dinners"`, producing `"plan my s for the week"`. Fixed with explicit plural replacements added before singular ones.

### `excludeTitle` fix in `SelectWithVarietyForModify`

The swap button was returning the same recipe — `SelectWithVarietyForModify` had no mechanism to avoid the current slot's recipe. Fixed by:

1. Extracting `currentTitle` before the search in `ModifyPlanAsync`
2. Passing it to `SelectWithVarietyForModify` as `excludeTitle`
3. Skipping any candidate matching `excludeTitle` before variety checks
4. Fallback returns first candidate that isn't the current recipe (not just `candidates[0]`)

```csharp
var currentTitle = plan.Days
    .FirstOrDefault(d => d.Day == targetDay)?.Slots
    .FirstOrDefault(s => s.SlotName == targetSlot)?.Recipe.Title;

var newRecipe = SelectWithVarietyForModify(candidates, plan, targetDay, targetSlot, currentTitle);
```

### React UI

**`MealPlanView.jsx` — new component:**

- Dynamic header: derives slot label from actual plan data (`"7-day full day plan"` vs `"7-day dinner & lunch plan"`)
- Renders all slots per day (not just first slot)
- Per-slot swap button — sends `"swap {day} {slot}"` to chat
- Day color coding (orange → amber → yellow → lime → teal → sky → violet)
- Protein emoji per slot (🍗 🥩 🥓 🐟 🥦)
- Dietary compatibility badge (✓ green / ⚠ red with violation category)

**`App.jsx` changes:**

- `sessionId` generated via `crypto.randomUUID()`, persisted in `sessionStorage` — stable per browser session
- `swapDay(day, slot)` handler sends `"swap {day} {slot}"` as a chat message
- `mealPlan` stored on assistant messages alongside `recipes`
- Welcome message updated to mention meal planning

**`ChatMessages.jsx` changes:**

- `onSwapDay` prop threaded through from `App`
- `MealPlanView` rendered below assistant message bubble when `msg.mealPlan` is present

### End-to-end chat test results

| Test | Message                                | Result                                                             |
| ---- | -------------------------------------- | ------------------------------------------------------------------ |
| TC1  | "plan my dinners for the week"         | ✅ CreateMealPlan, 1 slot/day, plan saved to Redis                 |
| TC2  | "swap Tuesday to something with pasta" | ✅ ModifyMealPlan, Pasta Salad returned, plan updated              |
| TC3  | "find me a chicken dinner"             | ✅ SearchRecipe, 5 recipes, existing flow unchanged                |
| TC4  | "plan my meals for the week"           | ✅ CreateMealPlan, 3 slots/day (breakfast+lunch+dinner)            |
| TC5  | "plan my breakfast and dinner"         | ✅ CreateMealPlan, 2 slots/day                                     |
| TC6  | Swap button click (Monday breakfast)   | ✅ ModifyMealPlan, targetSlot=breakfast, different recipe returned |

### Multi-slot Redis verification

```bash
docker compose exec redis redis-cli GET session:test-001:plan | jq '.days[0].slots | length'
# → 1  (dinner only)

docker compose exec redis redis-cli GET session:test-002:plan | jq '.days[0].slots | length'
# → 3  (breakfast + lunch + dinner)

docker compose exec redis redis-cli GET session:test-003:plan | jq '.days[0].slots | length'
# → 2  (lunch + dinner)
```

### Known Orchestrator limitations (deferred)

| Limitation                                   | Fix                                       |
| -------------------------------------------- | ----------------------------------------- |
| "swap it again" has no context               | Redis conversation history (Month 2)      |
| "show me my plan" goes to SearchRecipe       | Add `GetMealPlan` intent                  |
| "I can't have dairy" not extracted           | LLM entity extraction fallback (Month 2)  |
| Swap defaults to dinner on ambiguous message | Ask clarifying question or swap all slots |
| Response messages feel robotic               | LLM-generated summaries opt-in (Month 3)  |

---

## Files Created / Changed (Full Week)

```
src/shared/Models.cs                            # Updated: MealSlot, DayPlan, MealPlan, PlanConstraints, MealPlan on OrchestratorResponse
src/shared/SessionStore.cs                      # New: Redis-backed plan + profile persistence, TTL 7 days
src/agents/PlannerAgent/MealPlannerPlugin.cs    # New: GeneratePlanAsync, ModifyPlanAsync, variety enforcement, excludeTitle fix
src/agents/Orchestrator/IntentRouter.cs         # Updated: MealSlots, TargetSlot, TargetDay, ModifyConstraint, MealPlanSignals, query cleaning fix
src/agents/Orchestrator/AgentOrchestrator.cs    # Updated: HandleCreateMealPlanAsync, HandleModifyMealPlanAsync, SessionStore injected
src/api/ServiceRegistration.cs                  # Updated: AddMealPlannerAgent + AddRedis + SessionStore in AgentOrchestrator
src/api/Endpoints.cs                            # Updated: debug endpoints added (temp) + removed after testing
appsettings.json                                # Updated: Redis:ConnectionString added
src/frontend/src/components/MealPlanView.jsx    # New: 7-day grid, per-slot swap, protein emoji, dietary badges
src/frontend/src/App.jsx                        # Updated: sessionId, swapDay handler, mealPlan on messages
src/frontend/src/components/ChatMessages.jsx    # Updated: MealPlanView rendered, onSwapDay threaded
scripts/eval/test_planner.py                    # New: 15-case automated test runner
eval/datasets/planner_test_results.md           # New: test matrix with annotated results
docs/adrs/006-planner-agent-architecture.md     # New: 8 architectural decisions documented
```

---

## Concepts Learned

| Concept                         | What It Means                                                                                                                                                                                       |
| ------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Stateful vs stateless agents    | Stateless agents answer questions; stateful agents maintain things over time. The Planner is the first agent that _owns_ something between requests.                                                |
| Namespace collision             | `Recipe` was both a namespace and an intended type name — importing the namespace made the type reference ambiguous. Fix: use full namespace qualification, don't import the conflicting namespace. |
| Query rotation for diversity    | Sending different queries per day surfaces different parts of the vector space. Same query 7 times = near-identical results 7 times.                                                                |
| Variety as a post-search filter | Protein/cuisine variety is enforced _after_ retrieval, not during. Retrieval finds the best semantic match; variety selection picks the best match that doesn't repeat recent categories.           |
| Pre-computed slot metadata      | `ProteinCategory` and `CuisineTag` stored on `MealSlot` at generation time — avoid re-inferring on every subsequent comparison during modify flows.                                                 |
| Separate Redis keys per concern | Plan and profile stored under different keys — profile changes don't require re-serializing the full plan.                                                                                          |
| Null-returning reads            | `GetPlanAsync` returns `null` on missing/expired keys rather than throwing — callers handle the absence gracefully.                                                                                 |
| Generate-once, read-many        | Plan generation is expensive (14–45s depending on slots). Redis makes every subsequent read instant (1ms).                                                                                          |
| Immutable record update         | C# `init`-only records can't be mutated — use `Select` + `with` to rebuild only the changed node; everything else passes through unchanged.                                                         |
| Generation vs modify variety    | Generation looks back (future days don't exist yet). Modify checks both neighbors (full plan available). excludeTitle guarantees swap always returns a different recipe.                            |
| Safe-list pattern (reused)      | `CuisineFalsePositives` applies the same pattern as `DairyFalsePositives` from Week 3. Consistent across agents.                                                                                    |
| Stale cache masking fixes       | Always flush Redis when debugging inference logic changes — cached data from before the fix will make the fix appear broken.                                                                        |
| Test design vs system bugs      | TC13 failed because the test scenario was adversarial, not because the system was wrong.                                                                                                            |
| SessionId as user identity      | No auth in MVP — client-generated UUID in sessionStorage is the user identity. Trivially upgradeable to real auth later.                                                                            |
| Multi-slot intent extraction    | Natural language slot extraction ("breakfast and dinner") → `PlanConstraints.MealSlots` — no code change required to support new slot combinations.                                                 |
| Per-slot swap buttons           | UI swap sends day + slot name — Orchestrator extracts both, passes correct slot to ModifyPlanAsync. Avoids always defaulting to dinner.                                                             |

---

_The jump from stateless to stateful is the architectural inflection point of Month 2. Every decision — Redis key schema, TTL, what to store vs recompute, excludeTitle in variety selection, per-slot swap buttons — is a tradeoff between performance, correctness, and complexity. Week 5 is the most complex week in the portfolio._

---

## System Architecture Snapshot (End of Week 5 — Complete)

_Captured for use in ADR-006 and portfolio documentation._

```
User /chat message
    │
    ├─ IntentRouter                          [Week 4]
    │       rules <1ms · LLM fallback Month 2
    │
    ├─ AgentOrchestrator                     [Week 4]
    │       routes by intent · builds response
    │
    ├─────────────────────────────────────────────────────┐
    │                         │                           │
    ▼                         ▼                           ▼
Recipe Agent              Diet Agent              Planner Agent ★      [Week 5]
search · filter           rules 94%               generate · modify
rerank                    LLM fallback            variety enforcement
    │                         │                           │
    ├── Qdrant                ├── Rules engine            ├── calls Recipe ×7
    │   10K recipe vectors    │   420+ phrases            ├── calls Diet ×7
    │                         │   12 categories           │
    └── Ollama                └── Ollama                  └── Redis SessionStore ★
        nomic-embed-text          llama3.2 fallback            session:{id}:plan
                                                               TTL 7 days
    │
    └─ OrchestratorResponse
            recipes · dietary validation · message · meal plan
```

### Architectural thread across all 5 weeks

**"Rules first, LLM as fallback, opt-in for anything slow"** — applied consistently:

| Week | Fast path                   | LLM path                                 | Opt-in gate          |
| ---- | --------------------------- | ---------------------------------------- | -------------------- |
| 2    | Regex negation parsing      | Query expansion                          | `expand: true`       |
| 2    | Vector search               | LLM re-ranker                            | `rerank: true`       |
| 3    | Rules engine (94%)          | Unknown restrictions / allergy+ambiguity | Automatic escalation |
| 4    | Rules intent classifier     | LLM classification                       | Month 2              |
| 5    | Keyword variety enforcement | —                                        | Always-on            |

### Key infrastructure decisions

| Decision          | Choice                                  | Reason                                                                    |
| ----------------- | --------------------------------------- | ------------------------------------------------------------------------- |
| Vector DB         | Qdrant                                  | Self-hosted, gRPC, payload filtering                                      |
| Embeddings        | nomic-embed-text (768-dim)              | Free, Ollama-native, `search_document:`/`search_query:` prefix discipline |
| LLM               | llama3.2 via Ollama                     | CPU-runnable on 8GB RAM, opt-in to avoid bottleneck                       |
| Session state     | Redis                                   | Survives restarts, TTL-based expiry, O(1) reads                           |
| Diet knowledge    | Phrase-level rules + safe-lists         | Deterministic, extensible, zero LLM cost for 94% of cases                 |
| Cuisine inference | Keyword match + `CuisineFalsePositives` | Same safe-list pattern as Diet Agent — consistent across agents           |

### Agent summary

| Agent         | Type         | Primary method                    | LLM involved?              |
| ------------- | ------------ | --------------------------------- | -------------------------- |
| Recipe Agent  | Stateless    | Vector search + payload filter    | Opt-in (rerank, expand)    |
| Diet Agent    | Stateless    | Rules engine                      | Opt-in (ambiguous/unknown) |
| Orchestrator  | Stateless    | Rules intent classifier           | Opt-in (Month 2)           |
| Planner Agent | **Stateful** | Generate + Redis persist + modify | Opt-in via Diet Agent      |

### Performance profile (8GB RAM, CPU-only)

| Operation                   | Latency | Why                                       |
| --------------------------- | ------- | ----------------------------------------- |
| Vector search (1 query)     | ~2s     | Ollama embed on CPU                       |
| Plan generation (7 dinners) | ~17–20s | 7 × vector search sequential              |
| Plan read from Redis        | ~1ms    | In-memory key-value                       |
| Plan modify (1 slot)        | ~2s     | Redis load + 1 vector search + Redis save |
| Rules engine validation     | <1ms    | In-memory HashSet lookup                  |
| LLM re-rank / validation    | 30–300s | CPU inference bottleneck — opt-in only    |
