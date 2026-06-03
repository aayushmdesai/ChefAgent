# ADR-011: Evaluation Strategy

**Date:** June 2026  
**Status:** Accepted  
**Deciders:** Aayush Desai

---

## Context

ChefAgent needs a way to measure whether system changes make things better or worse. Without measurement, every change is a guess. The system has three distinct quality dimensions that require different measurement approaches:

- **Retrieval quality** — does vector search find the right recipes?
- **Response quality** — does the full pipeline produce useful, safe responses?
- **System performance** — is it fast enough for real use?

---

## Decision

Three evaluation layers, each measuring what the others cannot:

### Layer 1: RAGAS retrieval eval

**What it tests:** `/recipes/search` in isolation. Query → retrieved documents → RAGAS scores.

**Why:** Retrieval is the foundation. If vector search doesn't find relevant recipes, no amount of orchestration fixes it. RAGAS gives reproducible, numeric baselines that track across experiments.

**Metrics:** context_relevance (are retrieved docs relevant?), faithfulness (do answers stay grounded in docs?), answer_relevancy (does the response address the question?).

**Infrastructure:** `retrieve.py` runs locally, `score_simple.py` runs on Colab GPU (RAGAS requires an LLM judge — Colab avoids local RAM/compute constraints). `compare_experiments.py` shows deltas across experiments.

**Limitation:** Only tests retrieval. A system that retrieves perfectly but misclassifies intent still fails users. RAGAS cannot see intent classification, dietary validation, session state, or guardrails.

### Layer 2: E2E eval with LLM judge

**What it tests:** `/chat` — the full pipeline including intent classification, dietary validation, session state, and guardrails.

**Why:** Users interact through `/chat`, not `/recipes/search`. The full pipeline has 5+ layers between the user message and the recipe results. Each layer can fail independently. RAGAS cannot catch intent misclassification, session state failures, or guardrail gaps.

**Two sub-layers:**
- **Binary pass/fail harness** (`eval_e2e.py`): objective checks — intent classified correctly? recipes returned when expected? diet check ran? guardrails triggered correctly?
- **LLM judge** (`llm_judge.py`): subjective scores (1-5) for helpfulness, safety, coherence. Catches failures the binary harness misses — e.g. the system extracts a dietary constraint and classifies intent correctly but returns results that don't respect the constraint.

**Infrastructure:** 60-case golden dataset across 10 intent categories, including stateful sequences (setup_messages prime Redis state before the test message). Run-scoped session ID prefix prevents state collision across runs.

**Limitation:** LLM judge uses the same model (llama3.2) as the system being evaluated. The judge makes the same systematic errors as the system. Scores are directionally useful for comparing categories, not absolute quality claims. A stronger judge model (Claude, GPT-4) would remove this blind spot.

### Layer 3: Langfuse performance traces

**What it tests:** Latency, throughput, error rates per operation across all agents.

**Why:** A system that retrieves perfectly and produces great answers but takes 30 seconds fails users differently. Performance must be measured separately from quality. Langfuse gives operation-level latency (embedding, Qdrant, LLM calls, Redis) that aggregate metrics cannot provide.

**Infrastructure:** Self-hosted Langfuse v2 via Docker Compose. Fire-and-forget via `Channel<T>` + `IHostedService` — < 1ms tracing overhead. 14 span types across all agents. `embed.cache_hit` vs `embed.ollama` spans make cache effectiveness visible.

**Limitation:** Traces reflect the current hardware (8GB RAM, CPU-only Ollama). p95/p99 numbers will improve significantly on GPU deployment.

---

## Experiment tracking design

Each retrieval improvement is saved as a timestamped JSON file in `eval/experiments/`. `compare_experiments.py` loads two files and prints per-category deltas with directional arrows. This makes the improvement story concrete: "spell-check improved misspelling context_relevance by +0.175."

The experiment files are committed to the repo. The golden dataset is the source of truth — re-running the same experiment should produce similar (not identical, due to LLM judge variance) results.

---

## What we would change with budget

**Stronger judge model:** Replace llama3.2 with Claude or GPT-4 as the LLM judge. The blind spot disappears — a stronger judge can catch errors the system makes. The infrastructure is already built; the judge model is a one-line change.

**Larger golden dataset:** 100 retrieval queries and 60 e2e cases give meaningful category-level signals but individual category scores are noisy (6-10 cases per category). 500+ cases per layer would give reliable per-category scores.

**Automated regression testing:** Run the eval harness on every PR. Flag PRs that drop any category score below a threshold. Currently the eval is manual — running it requires knowing to run it.

**Human evaluation sample:** LLM judge scores are proxy metrics. Periodically sampling 20-30 responses for human review would calibrate whether the judge scores correlate with actual user satisfaction.

---

## Alternatives considered

**Single eval layer (RAGAS only):** Rejected. RAGAS cannot see intent classification, dietary validation, session state, or guardrails — the layers that actually fail in production.

**Single eval layer (e2e only):** Rejected. E2E eval doesn't isolate retrieval failures from classification failures. When a category scores poorly, you can't tell if retrieval or routing is the problem without the retrieval layer.

**OpenTelemetry + custom metrics instead of Langfuse:** Rejected for performance tracking. OTel has no first-class concept of an LLM generation (prompt, completion, tokens, latency). Langfuse's trace model maps to OTel concepts — switching later is feasible if needed.

---

## Consequences

- Three eval scripts must be run to get a complete picture. This is intentional — each measures something the others cannot.
- Experiment results are committed to the repo. This makes the improvement story reproducible and reviewable.
- The LLM judge limitation (same model) is accepted and documented. The infrastructure supports upgrading the judge model without code changes beyond the model string.