# ChefAgent — Week 6 Progress Log

**Date:** May 28, 2026
**Goal:** Memory and State Management — conversation history, profile persistence, GetMealPlan intent, LLM entity extraction
**Status:** ✅ Day 1 (Conversation History) Complete | ✅ Day 2 (Profile Persistence) Complete | ✅ Day 3 (GetMealPlan + Contractions) Complete | ✅ Day 4 (LLM Entity Extraction) Complete | ✅ Day 5 (Test Matrix) Complete | ✅ Days 6–7 (Resilience + ADR + v0.3.0) Complete

---

## What We're Building

Closing the remaining state gaps from Week 5 to make the system feel like it _remembers_ you. Every agent so far answers independently — Week 6 is what connects the turns into a conversation.

```
Week 5 gaps:
  "swap it again"           → no history → fails
  "show me my plan"         → no GetMealPlan intent → misroutes to SearchRecipe
  "I can't have dairy"      → no LLM entity extraction → constraint silently dropped
  profile resent every req  → SaveProfileAsync stubbed, never wired
  response messages robotic → template strings, no context awareness

Week 6 fixes:
  Day 1  → Conversation history in Redis (sliding window, structured entries)
  Day 2  → Profile persistence + GET /profile/{sessionId}
  Day 3  → GetMealPlan intent + contraction normalization
  Day 4  → LLM entity extraction for implicit dietary constraints
  Day 5  → Stateful flow test matrix (15 scenarios)
  Day 6–7 → Redis resilience + ADR-007 + v0.3.0
```

---

## Environment Note

Codespaces is now the primary dev environment. Ollama runs in Docker (not native). LLM calls (~30s on CPU) work end-to-end — features that were "hardware-limited" in Weeks 2–4 are now testable. Opt-in gates still appropriate since 30s is too slow for interactive use, but correctness can be verified.

---

## Day 1 — Conversation History in Redis

### What we built

Structured conversation history persisted in Redis after every `/chat` turn. Reference words in user messages (`"it"`, `"the first one"`, `"again"`) now resolve to concrete entities from prior turns — without any LLM calls.

### Why structured history (Option B) over raw messages (Option A)

Option A stores raw message text. Resolving `"is the first one vegan?"` would require sending conversation text to the LLM and hoping it extracts the right recipe title — slow, probabilistic, and expensive.

Option B stores structured entries with `RecipeTitles`, `Intent`, and `PlanId`. Resolution becomes deterministic: reference word detected → look up `history[-1].RecipeTitles[0]` → inject as search query → route to `ValidateDiet`. Zero LLM calls, ~1ms.

Same principle applied all along — rules first, LLM only when structure can't answer it.

### `ConversationEntry` record (`Models.cs`)

```csharp
public record ConversationEntry
{
    public required string Role { get; init; }           // "user" | "assistant"
    public required string Content { get; init; }        // raw message text
    public UserIntent? Intent { get; init; }             // null on user entries
    public List<string> RecipeTitles { get; init; } = []; // top recipes returned (assistant only)
    public string? PlanId { get; init; }                 // if a plan was returned
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
```

**Why `RecipeTitles` not full `RecipeDocument`?** Storing full recipe objects (ingredients, directions) would bloat each entry ~2KB × 5 recipes × 20 turns = 200KB just for history. Titles are enough to resolve `"the first one"` → `"Fettuccine Alfredo"` deterministically.

**Why `Intent` on assistant entries only?** When the IntentRouter sees a reference word, it looks back at the last assistant entry's intent to know what domain the user is referring to. `"is it vegan?"` after `SearchRecipe` → the `"it"` is a recipe. After `CreateMealPlan` → the `"it"` is a plan day.

### `SessionStore.cs` — history methods

```csharp
private const int MaxHistoryEntries = 20;

public async Task AppendMessageAsync(string sessionId, ConversationEntry entry)
{
    var key = HistoryKey(sessionId);
    var json = JsonSerializer.Serialize(entry, JsonOptions);
    await _db.ListRightPushAsync(key, json);
    await _db.ListTrimAsync(key, -MaxHistoryEntries, -1);  // sliding window
    await _db.KeyExpireAsync(key, DefaultTTL);             // refresh TTL on activity
}

public async Task<List<ConversationEntry>> GetHistoryAsync(string sessionId, int limit = 10)
{
    var entries = await _db.ListRangeAsync(HistoryKey(sessionId), -limit, -1);
    // deserialize and return...
}
```

Key schema: `session:{sessionId}:history` → Redis list, most recent entries at the right.

**Why `ListRightPush` + `ListTrim` not a JSON array string?** History is append-heavy — one entry per turn. A JSON string key requires deserialize-append-reserialize on every turn (O(n)). Redis list gives O(1) append and O(1) trim.

**Why store 20, read 10 by default?** Reference resolution needs 3–5 turns. LLM entity extraction (Day 4) may need more context. Storing 20 is cheap; the buffer means we never lose context unexpectedly.

**TTL refreshed on every append** — active sessions don't expire mid-conversation. A session last used 6 days ago that gets a new message resets to 7 days.

### `ClassifiedIntent.OriginalMessage` (`IntentRouter.cs`)

```csharp
public string OriginalMessage { get; init; } = string.Empty;
```

Not `required` — older construction sites still compile. Set to the raw user message before `.ToLowerInvariant()` and query cleaning. This is what gets saved to history, not the cleaned search query.

### `ResolveReferencesAsync` (`AgentOrchestrator.cs`)

Called in `RouteAsync` between saving the user message and routing to handlers:

```
AppendMessageAsync (user)
    ↓
ResolveReferencesAsync   ← new
    ↓
Route to handler
    ↓
AppendMessageAsync (assistant)
```

Reference word detection — `HashSet<string>`:

```
"it", "that", "this", "the first", "the second", "the third",
"first one", "second one", "third one",
"that one", "this one", "the one",
"again", "same", "that recipe", "that dish"
```

Resolution logic:

- Loads last 6 history entries (enough for 3 turns)
- Finds last assistant entry
- If last intent was `SearchRecipe` + recipe titles exist → resolve ordinal reference (`"second"` → index 1, else → index 0) → inject as `SearchQuery`, override intent to `ValidateDiet`
- If last intent was `ModifyMealPlan` + `"again"` → keep `ModifyMealPlan` intent (day recovery from raw text is unreliable — ask clarifying question)
- Graceful fallback: Redis failure → log warning, proceed without resolution

### Bug fixes discovered during testing

**`"pasta recipes"` → `"pasta s"`:** The replace chain had `.Replace(" recipe", " ")` which turned `"pasta recipes"` into `"pasta s"`. Fix: add `.Replace(" recipes", " ")` before `.Replace(" recipe", " ")` — plural before singular.

**Turn 2 intent stayed `SearchRecipe`:** The reference resolver only overrode intent when `classified.Intent == UserIntent.Unknown`. But `"is the first one vegan?"` was classified as `SearchRecipe` (rules-default) before resolution. Fix: override when intent is `Unknown` OR `SearchRecipe`.

### Verification

```bash
# Turn 1
curl -X POST http://localhost:5100/chat \
  -d '{"message": "find me pasta recipes", "sessionId": "hist-test-001"}'
# → SearchRecipe, 5 pasta recipes

# Redis check
docker compose exec redis redis-cli LLEN session:hist-test-001:history
# → 2 (user + assistant entries)

# Turn 2 — reference resolution
curl -X POST http://localhost:5100/chat \
  -d '{"message": "is the first one vegan?", "sessionId": "hist-test-001"}'
# → ValidateDiet, resolved to "Chinese Bake" (top pasta result), real violations returned

# Redis check after turn 2
docker compose exec redis redis-cli LLEN session:hist-test-001:history
# → 4
```

**Reference resolution added ~1ms (Redis read). Zero LLM calls.**

### Concepts Learned

| Concept                                  | What It Means                                                                                                 |
| ---------------------------------------- | ------------------------------------------------------------------------------------------------------------- |
| Structured vs raw history                | Storing intent + recipe titles enables deterministic reference resolution — no LLM needed for "the first one" |
| Redis list for append-heavy data         | O(1) push + trim vs O(n) deserialize-append-reserialize on a string key                                       |
| Sliding window                           | Keep last 20, read last 10 — buffer for future needs without unbounded growth                                 |
| TTL refresh on activity                  | Active sessions never expire mid-conversation; TTL resets on every append                                     |
| OriginalMessage before cleaning          | History stores raw text, not cleaned search query — what the user actually said                               |
| Reference resolution position            | Must run after saving user message but before routing — so the handler gets the resolved query                |
| Plural before singular in replace chains | `.Replace(" recipes", " ")` before `.Replace(" recipe", " ")` — order matters                                 |

---

## Files Created / Changed

```
src/shared/Models.cs                         # ConversationEntry record added
src/shared/SessionStore.cs                   # AppendMessageAsync, GetHistoryAsync, HistoryKey
src/agents/Orchestrator/AgentOrchestrator.cs # RouteAsync: save history + ResolveReferencesAsync
src/agents/Orchestrator/IntentRouter.cs      # OriginalMessage on ClassifiedIntent, recipes→recipe fix
```

---

---

## Day 2 — Profile Persistence + Rules Alignment

### What we built

Three things: wired `SaveProfileAsync`/`GetProfileAsync` into the Orchestrator, added `GET` and `POST /profile/{sessionId}` endpoints, and fixed a critical rules/allergy key mismatch that was causing unnecessary LLM escalations.

### Merge strategy — union with persistence

The profile merge flow on every `/chat`:

```
Request arrives with sessionId
    │
    ├─ Load stored profile from Redis
    ├─ Union merge: stored + request (request wins on conflicts)
    ├─ Save merged profile back to Redis   ← persists for all future turns
    └─ Use merged profile for all agent calls this turn
```

This means the user never has to re-send `"vegetarian"` — it's stored after the first time it appears and applied automatically on every subsequent request.

**Why union merge, not replacement?** If stored has `"vegetarian"` and request sends `"nut-free"` extracted from the message, both should apply. A replacement strategy would silently drop `"vegetarian"` on any turn where it wasn't explicitly re-sent.

### `LoadAndMergeProfileAsync` (`AgentOrchestrator.cs`)

```csharp
private async Task<DietaryProfile?> LoadAndMergeProfileAsync(
    string sessionId, DietaryProfile? requestProfile)
{
    var storedProfile = await _sessionStore.GetProfileAsync(sessionId);

    var merged = storedProfile is null ? requestProfile
        : requestProfile is null ? storedProfile
        : new DietaryProfile
        {
            Restrictions = requestProfile.Restrictions
                .Union(storedProfile.Restrictions, StringComparer.OrdinalIgnoreCase).ToList(),
            Allergies = requestProfile.Allergies
                .Union(storedProfile.Allergies, StringComparer.OrdinalIgnoreCase).ToList(),
            CuisinePreferences = requestProfile.CuisinePreferences
                .Union(storedProfile.CuisinePreferences, StringComparer.OrdinalIgnoreCase).ToList(),
        };

    if (merged is not null)
        await _sessionStore.SaveProfileAsync(sessionId, merged);

    return merged;
}
```

Graceful fallback on Redis failure — logs warning, continues without profile rather than throwing.

### New endpoints (`Endpoints.cs`)

```
GET  /profile/{sessionId}  → load stored profile (frontend calls on page startup)
POST /profile/{sessionId}  → save profile directly (sidebar toggle changes)
```

The `GET` endpoint is what lets the frontend restore sidebar toggles on page reload — reads `sessionId` from `sessionStorage`, fetches stored profile, populates toggles. User never has to re-configure their preferences.

### Critical bug fixed: `-free` restriction key mismatch

`IntentRouter` was extracting `"nut-free"` from `"nut-free pasta"` and storing it in `DietaryProfile.Restrictions`. But `DietaryRules.RestrictionCheckers` had no `"nut-free"` key — only `AllergyCheckers` had `"nuts"`.

Result: `CheckRestriction("nut-free")` → `TryGetValue` returns false → empty violations → signals unknown restriction → LLM escalation on every recipe.

**Fix:** Added `-free` variants to `RestrictionCheckers` in `DietaryRules.cs`:

```csharp
["nut-free"]    = i => CheckAgainstSet(i, NutIngredients,   "nuts",  "nut-free"),
["dairy-free"]  = i => CheckAgainstSet(i, DairyIngredients, "dairy", "dairy-free"),
["gluten-free"] = i => CheckAgainstSet(i, GlutenIngredients, "gluten", "gluten-free"),
["egg-free"]    = i => CheckAgainstSet(i, EggIngredients,   "eggs",  "egg-free"),
["soy-free"]    = i => CheckAgainstSet(i, SoyIngredients,   "soy",   "soy-free"),
```

**Performance impact:** nut-free pasta search: **151s → 2.9s** (3 LLM escalations eliminated).

Root cause: IntentRouter and DietaryRules used different key naming conventions (`"nut-free"` vs `"nuts"`). The fix aligned the restriction keys so the rules engine handles all `-free` patterns deterministically.

### Verification

```bash
# Set vegetarian profile
curl -X POST http://localhost:5100/profile/profile-test-001 \
  -d '{"restrictions": ["vegetarian"], "allergies": [], "cuisinePreferences": []}'
# → { "saved": true }

# Chat with no profile — uses stored vegetarian
curl -X POST http://localhost:5100/chat \
  -d '{"message": "find me a pasta dinner", "sessionId": "profile-test-001"}'
# → "3 are compatible with your vegetarian profile"

# Chat with nut-free in message — merges with stored
curl -X POST http://localhost:5100/chat \
  -d '{"message": "find me a nut-free pasta dinner", "sessionId": "profile-test-001"}'
# → "3 are compatible with your nut-free, vegetarian profile"

# Profile now has both
curl http://localhost:5100/profile/profile-test-001
# → { "restrictions": ["nuts", "vegetarian"] }

# Logs — all Layer=Rules, zero LLM escalations
# [DietAgent] Recipe='Pasta Ala Renee' Layer=Rules Time=8ms Violations=0 Compatible=true
# [Orchestrator] Time=2886ms (vs 151s before fix)
```

### Concepts Learned

| Concept                         | What It Means                                                                                                         |
| ------------------------------- | --------------------------------------------------------------------------------------------------------------------- |
| Union merge with persistence    | Merge stored + request profiles, save result back — user constraints accumulate across turns without re-sending       |
| GET endpoint for UI restore     | `GET /profile/{sessionId}` on page load restores sidebar state — user preferences survive tab close/reopen            |
| Key naming convention alignment | IntentRouter and DietaryRules must use the same restriction key names — mismatch silently falls through to LLM        |
| `-free` vs base allergen keys   | `"nut-free"` is a restriction (avoid nuts), `"nuts"` is an allergy (allergic to nuts) — same checker, different label |
| Performance impact of rules gap | One missing dictionary key → 3 unnecessary LLM calls → 151s instead of 2.9s per request                               |

---

---

## Day 3 — GetMealPlan Intent + Contraction Normalization

### What we built

Two things: a new `GetMealPlan` intent that retrieves a stored plan from Redis with zero agent calls, and a contraction normalization preprocessing step that fixes `"what's"` / `"whats"` / `"im"` style input before any signal word matching runs.

### Why contraction normalization is a preprocessing step (not signal variants)

The alternative was adding contracted variants to every signal set — `"what's my plan"` alongside `"what is my plan"`, `"whats my plan"` for the no-apostrophe case, etc. That's whack-a-mole: every new signal phrase needs multiple variants maintained forever.

Option A (normalization pass) runs once before all matching. Every signal set benefits automatically, including future ones. Same principle as the rules-first pattern — handle the general case cleanly once.

The normalization is not exhaustive — users type `"wuts"`, `"lemme see"`, `"gimme"`. The comment in the code acknowledges this explicitly: _"LLM classifier (Month 2) handles arbitrary phrasing."_ This is honest engineering — document what the code does and doesn't cover.

### `NormalizeContractions` (`IntentRouter.cs`)

```csharp
private static string NormalizeContractions(string lower) => lower
    .Replace("whats", "what is")      // no apostrophe — mobile common
    .Replace("what's", "what is")
    .Replace("how's", "how is")
    .Replace("hows", "how is")
    .Replace("i'm", "i am")
    .Replace("im ", "i am ")          // trailing space avoids matching "him", "slim"
    .Replace("can't", "cannot")
    .Replace("cant", "cannot")
    .Replace("don't", "do not")
    .Replace("dont", "do not")
    // ... full list of contractions + no-apostrophe variants
```

Called in `ClassifyAsync` right after `.ToLowerInvariant()`:

```csharp
var lower = message.ToLowerInvariant().Trim();
var normalized = NormalizeContractions(lower);

var intent = ClassifyIntent(normalized);           // ← normalized
var extractedProfile = ExtractProfile(normalized); // ← normalized
// SearchQuery and OriginalMessage still use lower — preserve what user typed
```

### `GetMealPlan` intent

Signal words checked **before** `CreateMealPlan` — more specific intent wins:

```csharp
private static readonly HashSet<string> GetMealPlanSignals =
[
    "show me my plan", "what is my plan", "my meal plan",
    "what am i eating", "what is for dinner this week",
    "show my plan", "get my plan", "view my plan",
    "my plan", "my weekly plan",
];
```

Handler is a pure Redis read — no agent calls:

```csharp
private async Task<OrchestratorResponse> HandleGetMealPlanAsync(ClassifiedIntent classified)
{
    var plan = await _sessionStore.GetPlanAsync(sessionId);
    if (plan is null)
        return "You do not have a meal plan yet. Want me to create one?";

    return plan with descriptive slot message;
}
```

### `UserIntent.GetMealPlan` added to enum (`Models.cs`)

Positioned between `CreateMealPlan` and `ModifyMealPlan` — logical grouping of plan-related intents.

### Verification

```bash
# Plan generation
curl ... '{"message": "plan my dinners for the week", "sessionId": "getplan-test-001"}'
# → CreateMealPlan, 5.2s (Ollama warm — embeds ~125ms vs 2-3s cold)

# GetMealPlan with contraction
curl ... '{"message": "what'\''s my plan?", "sessionId": "getplan-test-001"}'
# → GetMealPlan, 32ms, hasPlan: true

# GetMealPlan without plan
curl ... '{"message": "show me my plan", "sessionId": "no-plan-session"}'
# → GetMealPlan, 2ms, helpful fallback message
```

**GetMealPlan is a pure Redis read: 2–32ms. Zero agent calls. Zero LLM calls.**

### Performance note

Plan generation dropped from 14–17s (Week 5) to **5.2s** (Day 3). Same code — Ollama model is now warm in the Codespaces container, embed calls taking ~125ms instead of 2–3s. This is the normal operating speed going forward.

### Concepts Learned

| Concept                         | What It Means                                                                                                                                                                        |
| ------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Normalization before matching   | One preprocessing pass benefits all signal sets — no per-signal variant maintenance                                                                                                  |
| More specific intent first      | `GetMealPlan` checked before `CreateMealPlan` — both contain "my plan", but one is retrieval, one is generation                                                                      |
| Honest limitation documentation | NormalizeContractions comment explicitly says it's not exhaustive and points to Month 2 LLM fix                                                                                      |
| Read vs generate                | `GetMealPlan` never calls agents — the plan is already in Redis. Separating read from generate as distinct intents avoids re-generating a plan the user already has                  |
| Warm vs cold Ollama             | Cold start: 2–3s per embed. Warm (model loaded): ~125ms. Architecture decisions made under cold-start constraints are still valid — but performance improves significantly once warm |

---

---

## Day 4 — LLM Entity Extraction for Implicit Constraints

### What we built

A context-aware LLM fallback that extracts dietary constraints from natural language expressions that the rules engine can't catch. Passes conversation history and the existing stored profile so the LLM has full context and doesn't re-extract already-known constraints.

### Why rules alone aren't enough

Rules catch explicit dietary vocabulary: `"gluten-free pasta"`, `"vegan dinner"`. They miss natural human expression:

- `"I cannot have dairy"` — personal limitation statement
- `"something plant-based"` — lifestyle descriptor
- `"I am allergic to peanuts"` — allergy expressed in natural language
- `"I follow a vegetarian diet"` — diet expressed as a habit

The gap isn't fixable with more signal words — it requires understanding meaning, not matching vocabulary. That's LLM territory.

### Detection heuristic — `HasImplicitConstraintSignal`

Runs before every LLM call. Cheap string check (~0ms). Only fires the LLM if the message contains implicit constraint signals:

```csharp
private static readonly HashSet<string> ImplicitConstraintSignals =
[
    "i cannot", "i can not", "i do not eat", "i dont eat",
    "i am allergic", "i am intolerant", "i am sensitive",
    "plant-based", "clean eating", "healthy eating",
    "i follow", "we follow", "i only eat",
    "no dairy", "no gluten", "no nuts", "no meat",
    // ... full list
];
```

Three conditions must all be true before LLM is called:

1. Rules extracted nothing (`extractedProfile is null`)
2. Implicit signal detected
3. 90s timeout budget available

### `TryExtractProfileWithLlmAsync`

Builds a prompt with three pieces of context:

```
Known profile (do NOT re-extract): {existingProfile}
Conversation history (last 6 turns): {history}
Current message: {message}
```

The LLM returns structured JSON with a confidence field:

```json
{
  "restrictions": ["vegetarian"],
  "allergies": ["dairy"],
  "uncertain": ["healthy"],
  "confidence": "high"
}
```

`confidence=low` → don't apply, return null. `confidence=high/medium` → merge into profile.

**JSON extraction fix:** LLM sometimes appends explanation text after the JSON object. Fixed by extracting `raw[braceStart..(braceEnd+1)]` — only the first complete JSON object, ignoring anything after.

### Context-aware extraction — why history matters

Without history: `"something plant-based"` has no prior context — LLM guesses.

With history: if turn 2 said `"I've been vegetarian for years"`, the LLM on turn 5 has that context when the user says `"something plant-based"` — confident `vegetarian` extraction.

Existing profile is passed explicitly so the LLM doesn't re-extract `"vegetarian"` on turn 6 when it's already stored — keeps output clean and avoids duplicate merges.

### Test results

| Test          | Message                                                            | Extracted                                   | Notes                           |
| ------------- | ------------------------------------------------------------------ | ------------------------------------------- | ------------------------------- |
| T1            | `"i cannot have dairy, find me dinner"`                            | `allergies: [dairy]`                        | ✅ Correct                      |
| T2            | `"something plant-based for dinner"`                               | `restrictions: [vegetarian, vegan]` + extra | ⚠️ Over-extracted nuts/gluten   |
| T3            | `"i am allergic to peanuts"`                                       | `allergies: [peanuts]`                      | ✅ Rules caught it (regex)      |
| T4 multi-turn | Turn 1: `"i follow a vegetarian diet"` → Turn 2: `"find me pasta"` | `restrictions: [vegetarian]` persisted      | ✅ Profile carried across turns |

### Known limitations (deferred to Month 3)

| Limitation                            | Root cause                                                                                        | Fix                                                 |
| ------------------------------------- | ------------------------------------------------------------------------------------------------- | --------------------------------------------------- |
| Over-extraction on semantic terms     | LLM hallucinates constraints beyond what was stated (`"plant-based"` → also extracts nuts/gluten) | Prompt tuning — add negative examples               |
| Query not cleaned for implicit prefix | `"i follow a diet"` left after stripping `"vegetarian"`                                           | Proper NLP parsing or regex for full phrase removal |
| `_ollamaUrl` / `_chatModel` naming    | Implementation leaks into abstraction layer                                                       | Rename to `_llmUrl` / `_llmModel` in Month 3        |
| 90s timeout blocks response           | LLM extraction runs synchronously before routing                                                  | Make async with background profile update           |

### Concepts Learned

| Concept                            | What It Means                                                                                                             |
| ---------------------------------- | ------------------------------------------------------------------------------------------------------------------------- |
| Heuristic gate before LLM          | Cheap signal detection prevents LLM call on every request — only pay the 90s cost when there's a reason                   |
| Context-aware extraction           | History + known profile in prompt = LLM extracts only new constraints, not re-extracts known ones                         |
| JSON brace extraction              | `raw[braceStart..(braceEnd+1)]` extracts first valid JSON object — handles LLM appending explanation text                 |
| Confidence field                   | Lets the LLM signal uncertainty — `low` → don't apply, `high` → merge. Prevents hallucinated constraints from being saved |
| Rules still win for explicit terms | `"allergic to peanuts"` was caught by rules regex, not LLM — LLM is the fallback, not the primary                         |

---

## Files Created / Changed (Days 1–4)

```
src/shared/Models.cs                         # ConversationEntry record, UserIntent.GetMealPlan added
src/shared/SessionStore.cs                   # AppendMessageAsync, GetHistoryAsync, HistoryKey
src/agents/Orchestrator/AgentOrchestrator.cs # RouteAsync: history + profile merge + ResolveReferencesAsync
                                             # HandleGetMealPlanAsync added
src/agents/Orchestrator/IntentRouter.cs      # NormalizeContractions, GetMealPlanSignals,
                                             # ImplicitConstraintSignals, TryExtractProfileWithLlmAsync,
                                             # OriginalMessage on ClassifiedIntent, query cleaning fixes
src/agents/Diet/DietaryRules.cs              # nut-free, dairy-free, gluten-free, egg-free, soy-free added
src/api/Endpoints.cs                         # GET + POST /profile/{sessionId}, history passed to ClassifyAsync
```

---

---

## Day 5 — Stateful Flow Test Matrix

### Setup

15 test cases across 5 groups. Script: `scripts/eval/test_memory.py`. Uses `/chat`, `/profile`, and Redis CLI directly.

### Results

| TC  | Scenario                                                     | Status |
| --- | ------------------------------------------------------------ | ------ |
| 01  | Reference resolution — "is the first one vegan?"             | ✅     |
| 02  | Ordinal reference — "is the second one gluten-free?"         | ✅     |
| 03  | Reference with no prior history — graceful fallback          | ✅     |
| 04  | Stored profile applied on /chat with no profile in request   | ✅     |
| 05  | Profile union merge — stored vegetarian + extracted nut-free | ✅     |
| 06  | GET /profile returns stored preferences                      | ✅     |
| 07  | "what's my plan?" returns stored plan (GetMealPlan)          | ✅     |
| 08  | GetMealPlan with no plan → helpful fallback message          | ✅     |
| 09  | "my plan" returns same plan, not re-generated                | ✅     |
| 10  | LLM extracts implicit dairy constraint                       | ✅     |
| 11  | Multi-turn constraint persistence across turns               | ✅     |
| 12  | Explicit term → rules only, LLM not called                   | ✅     |
| 13  | Unknown sessionId on GET /profile → 404                      | ✅     |
| 14  | History sliding window — 22 messages → LLEN=20               | ✅     |
| 15  | Full 6-step end-to-end stateful flow                         | ✅     |

**15/15 passed. 0 system bugs.**

### TC10 investigation

TC10 initially failed — `dairy` not appearing in profile, `llm_fired=False`. Root cause was two separate issues found during investigation:

**Issue 1 — Query cleaning:** `"i cannot have dairy, find me dinner"` → generic `.Replace("i cannot have ", "")` ran before specific full-phrase replacement, leaving `"dairy"` as the search query. Fixed by adding specific full-phrase replacements before the generic prefix.

**Issue 2 — Log timing in test:** The `llm_fired` check reads recent logs — when Ollama was busy with a prior request, the LLM extraction log scrolled out of the tail window. Test design issue, not a system bug.

After fixes: `"i cannot have dairy, find me dinner"` → query=`"dinner"`, `dairy` extracted into profile. Over-extraction of extra constraints (`nuts`, `gluten`) is the known LLM hallucination issue — deferred to Month 3 prompt tuning.

### TC15 — Full end-to-end flow

6 steps, all passing:

```
Set profile (vegetarian)
    → Search "find me pasta" → vegetarian applied from Redis
    → "is the first one vegan?" → ValidateDiet, resolved to "Pasta Ala Renee"
    → "plan my dinners for the week" → planId=316cdf55 generated
    → "what's my plan?" → GetMealPlan, same planId returned
    → "swap Tuesday to pasta" → ModifyMealPlan, plan updated
```

This is the complete Week 6 feature set working end-to-end in a single conversation.

### Key interview talking points

**"15/15 with the one TC10 failure being a test design issue (log timing) plus a query cleaning bug found and fixed during investigation."**

**"TC15 proves the full stateful flow works: profile persists, history resolves references, plan generates and retrieves, swap updates correctly — all in one conversation thread."**

**"The test matrix found a real bug: generic prefix replacement running before specific full-phrase removal left 'dairy' as the search query. Caught by testing, fixed in the same session."**

---

## Files Created / Changed (Days 1–5)

```
src/shared/Models.cs                         # ConversationEntry record, UserIntent.GetMealPlan added
src/shared/SessionStore.cs                   # AppendMessageAsync, GetHistoryAsync, HistoryKey
src/agents/Orchestrator/AgentOrchestrator.cs # RouteAsync: history + profile merge + ResolveReferencesAsync
                                             # HandleGetMealPlanAsync added
src/agents/Orchestrator/IntentRouter.cs      # NormalizeContractions, GetMealPlanSignals,
                                             # ImplicitConstraintSignals, TryExtractProfileWithLlmAsync,
                                             # OriginalMessage on ClassifiedIntent, query cleaning fixes
src/agents/Diet/DietaryRules.cs              # nut-free, dairy-free, gluten-free, egg-free, soy-free added
src/api/Endpoints.cs                         # GET + POST /profile/{sessionId}, history passed to ClassifyAsync
scripts/eval/test_memory.py                  # New: 15-case stateful flow test runner
eval/datasets/memory_test_results.md         # New: test matrix with annotated results
```

---

---

## Days 6–7 — Redis Resilience + ADR-007 + v0.3.0

### Redis resilience

Every Redis call in the Orchestrator is now wrapped in try/catch. Redis failure degrades to stateless mode — no history, no profile, no plan — but core recipe search continues working. Never propagates to 500.

**The bug that exposed the gap:** All three try/catch fixes were added to the code but Step 1 (`AppendMessageAsync`) wasn't saved before the rebuild. The Redis-down test returned a full stack trace instead of a graceful response. Fixed by verifying the exact lines in the file before rebuilding.

**Failure behavior per operation:**

| Operation                        | Redis down behavior                                       |
| -------------------------------- | --------------------------------------------------------- |
| `AppendMessageAsync` Steps 1 + 5 | Skip — history not saved this turn                        |
| `GetHistoryAsync`                | Return empty list — no reference resolution               |
| `LoadAndMergeProfileAsync`       | Use request profile only — no stored profile              |
| `GetPlanAsync`                   | Return null — treat as no plan                            |
| `SavePlanAsync`                  | Log warning — plan returned to user even if not persisted |

**Critical design decision — `SavePlanAsync` split out of main try/catch:**

```csharp
// Before: SavePlanAsync inside the main try/catch
// Redis failure → entire CreateMealPlan handler returns error
// User loses a successfully generated plan

// After: SavePlanAsync in its own try/catch
var plan = await _plannerAgent.GeneratePlanAsync(...);
try { await _sessionStore.SavePlanAsync(sessionId, plan); }
catch (Exception ex) { _logger.LogWarning(...); /* continue */ }
// Plan returned to user even if Redis save fails
```

**Verification:**

```bash
docker compose stop redis

curl -X POST http://localhost:5100/chat \
  -d '{"message": "find me a chicken dinner", "sessionId": "resilience-test"}'
# → {"message": "Here are 5 recipes for \"a chicken\".", "intent": "SearchRecipe"}
# No 500. No exception. Stateless degradation confirmed.

docker compose start redis
```

### ADR-007

8 architectural decisions documented in `docs/adrs/007-session-memory-design.md`:

1. Structured history over raw message text
2. Redis list for append-heavy history data
3. Deterministic reference resolution (no LLM)
4. Union merge profile persistence
5. LLM entity extraction as gated fallback
6. Contraction normalization as preprocessing step
7. `GetMealPlan` as distinct intent
8. Graceful stateless fallback on Redis failure

### Week 6 metrics

| Metric                       | Value                                                          |
| ---------------------------- | -------------------------------------------------------------- |
| New Redis keys               | `session:{id}:history` (list), `session:{id}:profile` (string) |
| History window               | Store 20, read 10 default, buffer 20                           |
| Reference resolution latency | ~1ms (Redis read, zero LLM)                                    |
| Profile persistence          | Union merge, saved on every turn                               |
| LLM extraction timeout       | 90s, graceful fallback                                         |
| Resilience                   | Redis down → stateless, never 500                              |
| Test cases                   | 15/15 passed                                                   |
| Real bugs found              | 2 (query cleaning, try/catch missing from build)               |
| ADRs written                 | 1 (ADR-007)                                                    |
| Git tag                      | v0.3.0                                                         |

### Concepts Learned

| Concept                             | What It Means                                                                                                               |
| ----------------------------------- | --------------------------------------------------------------------------------------------------------------------------- |
| Graceful degradation layers         | Each Redis operation fails independently — history miss ≠ profile miss ≠ plan miss                                          |
| Split try/catch for independence    | Generation and persistence are separate concerns — Redis failure shouldn't discard a successfully generated plan            |
| Stateless fallback as a design goal | The system should work without Redis — just without memory. Core functionality is never Redis-dependent                     |
| Verify before rebuild               | Try/catch added to code but not saved → stale build → Redis-down returns 500. Always verify file contents before rebuilding |

---

## Files Created / Changed (Full Week)

```
src/shared/Models.cs                         # ConversationEntry record, UserIntent.GetMealPlan added
src/shared/SessionStore.cs                   # AppendMessageAsync, GetHistoryAsync, HistoryKey
src/agents/Orchestrator/AgentOrchestrator.cs # RouteAsync: history + profile + ResolveReferencesAsync
                                             # HandleGetMealPlanAsync, Redis resilience (all operations wrapped)
src/agents/Orchestrator/IntentRouter.cs      # NormalizeContractions, GetMealPlanSignals,
                                             # ImplicitConstraintSignals, TryExtractProfileWithLlmAsync,
                                             # OriginalMessage, query cleaning fixes, specific prefix order fix
src/agents/Diet/DietaryRules.cs              # nut-free, dairy-free, gluten-free, egg-free, soy-free added
src/api/Endpoints.cs                         # GET + POST /profile/{sessionId}, history passed to ClassifyAsync
scripts/eval/test_memory.py                  # New: 15-case stateful flow test runner
eval/datasets/memory_test_results.md         # New: test matrix with annotated results
docs/adrs/007-session-memory-design.md       # New: 8 architectural decisions
```

---

## Week 6 Complete

- [x] Day 1: Conversation history in Redis
- [x] Day 2: Profile persistence + rules alignment fix (151s → 2.9s)
- [x] Day 3: GetMealPlan intent + contraction normalization
- [x] Day 4: LLM entity extraction for implicit constraints
- [x] Day 5: Stateful flow test matrix — 15/15
- [x] Days 6–7: Redis resilience + ADR-007 + v0.3.0 tagged
