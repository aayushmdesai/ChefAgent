# ADR-008: Guardrails Architecture

**Date:** May 29, 2026
**Status:** Accepted
**Context:** Week 7 — adding input validation, output validation, cost controls, rate limiting, and confidence signaling to make ChefAgent production-grade

---

## Context

Through Weeks 1–6, every agent assumed good input and produced trusted output. The system had no defense against malicious input, no validation of LLM responses, no protection against Ollama outages, and no way to communicate uncertainty to the user. Week 7 adds skepticism — five guardrail layers that validate, protect, and signal.

---

## Decision 1: Five-Layer Guardrail Architecture

**Chosen:** Five independent layers, each with a single responsibility.

```
Request → InputGuard → RateLimiter → RepeatCheck → Classify → Route
                                                        ↓
                                            CircuitBreaker (LLM calls)
                                            OutputGuard (LLM responses)
                                                        ↓
                                            AppendConfidenceDisclaimer
                                                        ↓
                                            GuardrailAuditLog (all layers)
```

**Why five layers, not one "GuardrailMiddleware"?** Each layer has different concerns, different state requirements, and different failure modes. InputGuard is stateless and static. RateLimiter is stateful and in-memory. CircuitBreaker is a singleton shared across agents. Combining them would violate single responsibility and make testing harder.

**Why this ordering?** Each layer is cheaper than the next. A blocked injection never hits the rate limiter. A throttled session never hits the Orchestrator. Cost increases left to right.

---

## Decision 2: Two-Signal Prompt Injection Detection

**Chosen:** Require BOTH a trigger verb ("ignore", "disregard") AND a system-directed target ("instructions", "your role") before blocking.

**Rejected:** Single-signal detection (block on trigger verb alone).

**Why:** Single-signal blocks `"ignore the garlic and add more basil"` — a legitimate cooking query. In a food domain, instruction verbs appear naturally in cooking context. Two-signal detection eliminates this entire class of false positives by requiring the verb to target the system, not food.

**Why not LLM-based detection?** Same principle as the Diet Agent: rules handle the common cases instantly. LLM detection would add 30+ seconds per request on CPU. The rules catch the known patterns; sophisticated attacks that bypass rules would also likely bypass an LLM detector.

---

## Decision 3: 200 OK for Injection, 429 for Rate Limiting

**Chosen:** Injection returns `200 OK` with a neutral redirect. Rate limiting returns `429 Too Many Requests`.

**Why different status codes?** Different threat models:

- **Injection:** The attacker is probing for bypass vectors. Returning `400 Bad Request` or any error tells them their injection was detected, giving them signal to iterate. A `200` with a bland "I can help you find recipes..." is indistinguishable from a normal non-food response.
- **Rate limiting:** The client needs to know to back off. `429` is the correct HTTP semantic — the client can implement exponential backoff. Hiding rate limiting behind `200` would cause confused retry loops.

---

## Decision 4: OutputGuard as Instance Class, InputGuard as Static

**Chosen:** `InputGuard` is static (no dependencies, validates one shape). `OutputGuard` is a DI-registered singleton (multiple validation shapes, needs logger for retry/fallback counters).

**Why:** InputGuard validates a string the same way every time. OutputGuard validates reranker JSON differently than entity extraction JSON, needs to track retry/fallback counts, and will eventually integrate with Langfuse. Static classes can't take constructor-injected dependencies.

---

## Decision 5: Circuit Breaker — Singleton, Three States, Optional Calls Only

**Chosen:** One `CircuitBreaker` singleton shared across all agents. Three states: Closed → Open → HalfOpen. Protects optional LLM calls only, not core embedding.

**Why singleton?** One Ollama instance = one failure domain. If Ollama is down for `DietValidationPlugin`, it's down for `IntentRouter` and `RecipeReranker` too. Per-agent breakers would trip independently for the same root cause.

**Why three states, not two?** Open/Closed alone means LLM is either fully available or fully blocked. HalfOpen allows testing recovery with a single call before routing all traffic back. Without HalfOpen, recovery requires manual intervention or a blind timer.

**Why not protect embedding?** `GetEmbeddingAsync` calls Ollama's `/api/embed` — required for vector search. There's no rules-based fallback for "convert text to 768-dim vector." The circuit breaker protects calls that have fallbacks (reranking → vector order, entity extraction → rules-only, GeneralQuestion → error message). Embedding has no fallback — if it fails, search genuinely can't work.

---

## Decision 6: In-Memory Rate Limiting, Not Redis

**Chosen:** `ConcurrentDictionary<string, SessionWindow>` with per-entry locks.

**Rejected:** Redis-backed rate limiting.

**Why:** Rate limiting state is ephemeral — it doesn't need to survive server restarts. If the server restarts, all rate limit windows reset, which is acceptable. Redis adds network latency to every request for state that's inherently short-lived. In-memory `ConcurrentDictionary` is O(1) and zero-latency.

**Why `ConcurrentDictionary` with per-entry locks?** The dictionary handles cross-session concurrency. The per-window lock handles within-session concurrency (multiple concurrent requests from the same session racing on the queue).

**Scaling concern:** In a multi-instance deployment, each instance has its own rate limiter — a user could get 30 requests/min per instance. The fix is Redis-backed rate limiting (consistent across instances). For single-instance MVP, in-memory is correct.

---

## Decision 7: ResponseConfidence as Handler-Level Metadata

**Chosen:** Each handler sets `ResponseConfidence` (High/Medium/Low) based on which path was taken. No new tracking infrastructure.

**Why handler-level, not agent-level?** The handler already knows everything: Was a profile present? Did the Diet Agent succeed? Did the LLM fail? Confidence is a label on information the handler already has, not new state to track.

| Path taken                                             | Confidence |
| ------------------------------------------------------ | ---------- |
| Rules-only, no LLM involved                            | High       |
| LLM called, output validated                           | Medium     |
| LLM failed, fallback triggered, or dietary unavailable | Low        |

**Why on the response, not in metadata?** Frontend needs it for rendering (show/hide disclaimers). Burying it in a metadata dictionary requires the frontend to parse nested structures. A top-level field is direct.

---

## Decision 8: GuardrailAuditLog as In-Memory Ring Buffer

**Chosen:** `ConcurrentQueue<GuardrailEvent>` capped at 1000 entries. Queryable via `GET /admin/guardrails`.

**Rejected:** File-based logging, Redis-backed audit.

**Why in-memory?** Audit events are operational data, not durable records. In production, these would stream to Langfuse or a logging service. For MVP, the in-memory buffer provides observability without infrastructure overhead. The `/admin/guardrails` endpoint is useful for demos, debugging, and as the Month 3 observability seed.

**Why 1000 entries?** At 30 requests/min max per session, 1000 entries covers ~33 minutes of peak single-session traffic or several hours of normal usage. Enough for debugging without unbounded memory growth.

**Why not use the existing ILogger?** ILogger writes to stdout — useful but not queryable. The audit log is structured (EventType, SessionId, Detail, Timestamp) and queryable via the admin endpoint. Both exist: ILogger for ops, audit log for the application layer.

---

## Consequences

### Positive

- Five independent layers — each testable, each with clear responsibility
- 18/18 integration test passing across all layers
- Two-signal injection detection: zero false positives on cooking queries
- Circuit breaker eliminates 100+ second timeout waits when Ollama is down
- Rate limiting is per-session with session isolation verified
- Confidence signals give the UI and user honest uncertainty communication
- Audit log provides observable guardrail triggers for debugging and demos

### Negative

- In-memory rate limiting doesn't scale to multi-instance deployment
- Circuit breaker can't protect core embedding (no fallback exists)
- Two-signal detection won't catch sophisticated prompt injections (e.g. encoded instructions, role-play chains)
- Audit log loses history on server restart

### Future Work

- Month 3: Wire audit events to Langfuse for persistent observability
- Month 3: Add pre-computed query cache as embedding fallback when Ollama is down
- Consider Redis-backed rate limiting if multi-instance deployment is needed
- Add LLM-based injection detection as a second layer behind rules (same pattern as Diet Agent)
