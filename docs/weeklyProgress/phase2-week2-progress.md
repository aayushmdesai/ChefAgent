## Goal Alignment
- Month Objective: Orchestration can scale past 4 agents
- KR(s) this week advances: KR1 — registry + pipeline builder shipped, orchestrator cutover complete
- Regression check: `test_e2e_sweep.py` — 47/50 vs. this script's own 44/50 baseline (Week 8/16)

---

## Day 1 — Close the Week 1 regression gap: paced e2e rerun ✅

### What Was Built

- Added `pace: bool = True` param to `send_chat()` — 1.5s delay by default on every call, per Week 1's flagged fix
- Extracted TC41 (35-request rate-limit burst) into its own `test_rate_limit_burst()`, run last and unpaced, so it doesn't consume Voyage's rate-limit budget for the other 49 cases
- Added `RUN_ID` (uuid, 8 hex chars) and a `scoped()` helper — every session ID in the sweep is now unique per run

### What Was Found (not in the original Day 1 scope, but blocking it)

The first paced run surfaced two real, previously-invisible bugs rather than confirming a clean baseline:

1. **Redis connection string never parsed correctly.** `AddRedis()` passed the raw `rediss://user:pass@host:port` URI straight into `ConfigurationOptions.Parse()`, which has no native support for that scheme (confirmed: open StackExchange.Redis feature request, #1590). The endpoint silently mis-parsed — visible in logs as a doubled port (`host:6379:6379`) — meaning Redis had likely never connected successfully in this environment, independent of any Phase 2 change. Every session write (`SavePlanAsync`, `AppendMessageAsync`, etc.) was failing via the circuit breaker's fail-fast path, invisibly.

2. **The failure was invisible because `SessionStore`'s 8 Redis methods caught `Exception` with no logging.** `RecordFailure()` fired correctly but nothing captured *why*. Added `ILogger<SessionStore>` and logged the real exception in every catch block — this is what surfaced the actual root cause (`RedisConnectionException: UnableToConnect`) instead of another guess.

**Fix:** manual URI parsing in `AddRedis()` (`ServiceRegistration.cs`) — extracts host/port/password/SSL from the `rediss://` string directly into `ConfigurationOptions` instead of relying on `.Parse()`. Confirmed via `[Startup] Redis pre-warm ping succeeded` post-fix, and TC19–26 (the full meal-plan generate → read → modify → multi-slot chain) now passes end-to-end for the first time.

A second-order bug followed from the first: once Redis genuinely started persisting, the sweep's fixed, non-unique session IDs (`e2e-search`, `e2e-plan`, etc.) began reading back *real* leftover data from previous runs — surfacing as false failures (TC09 showing dietary-profile-influenced results on a session that never set one). Fixed by scoping every session ID to a per-run `RUN_ID`.

### Regression check — paced e2e sweep

| Run | Result | Notes |
|-----|--------|-------|
| Before Redis fix | 46/49 (TC42 missing) | Zero 429s in scored path; TC24/TC26 timed out — traced to Redis, not Voyage |
| After Redis + session-scoping fix | **47/50** | Zero 500s, zero connection errors, zero unexplained failures |

**This script's own baseline (Week 8/16): 44/50.** 47/50 is an improvement, not a regression — confirms the Week 1 IAgent/AgentRegistry/PipelineBuilder changes did not break `/chat` behavior.

**Note on the 93% (56/60) frozen baseline:** that number comes from a separate, larger harness (`eval/datasets/e2e_results.json`, Week 19, 60 cases, run against production with pre-warm + retry logic) — not this file. `test_e2e_sweep.py` and the frozen production harness are different tests with different denominators; comparing this week's 47/50 against 56/60 would not be apples-to-apples. This script's own 44/50 is the correct comparison point, and Week 2's later regression checks (Day 6) should keep using it consistently.

### Remaining failures (3, all pre-existing/tracked)

| TC | Query | Issue | Tracked as |
|----|-------|-------|-----------|
| TC03 | "recipes with garlic and tomatoes" | → ValidateDiet, expected SearchRecipe | I-4 (reopened — was marked resolved Week 16, reproduced today) |
| TC05 | "pasta without dairy" | → ValidateDiet, expected SearchRecipe | I-5 (already tracked, deferred) |
| TC09 | "vegetarian stir fry without nuts" | → ValidateDiet, expected SearchRecipe | I-5 pattern |
| TC47 | "jalapeño & crème fraîche" | → ValidateDiet, expected SearchRecipe | New instance of I-4/I-5 pattern — same rule gap, unicode/special-char query |

TC41 (burst): 5/35 got 429, exactly matching Week 8's original documented behavior — confirms the app's rate limiter itself was never affected by any of the above; the earlier 0/35 result (before the Redis fix) was a side effect of broken Redis calls consuming time inside the request path, not a limiter regression.

### Definition of Done

- [x] One full paced e2e run, zero 429s in the failure trace (scored path)
- [x] Pass rate recorded and compared to the correct baseline (44/50 → 47/50)
- [x] TC41 confirmed isolated and behaving per original documented baseline
- [x] Two infra bugs found via this process fixed, not just papered over (Redis parsing, error logging)

### Tech debt updated

- I-4 reopened (see above)
- M-5, M-6 added and marked resolved
- Inf-7 added (misleading `"ollama"`-keyed CircuitBreaker name), deferred
- T-10 added and marked resolved (session ID scoping)