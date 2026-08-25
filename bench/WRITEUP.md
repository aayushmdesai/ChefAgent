# What I could and couldn't measure about Nebius Token Factory

I ported a multi-agent RAG system's inference to [Nebius Token Factory](https://tokenfactory.nebius.com) and benchmarked it. This writeup reports three things I can defend, one thing I expected to measure and couldn't, and seven friction points from the first two hours on the platform.

The harness, the query set, and every raw result — including the runs that failed and the ones that were inconclusive — are in [`bench/`](./). The full working log, with each finding tagged VERIFIED or HYPOTHESIS, is in [`FINDINGS.md`](./FINDINGS.md).

---

## The system

[ChefAgent](https://github.com/aayushmdesai/ChefAgent) is a multi-agent cooking assistant in C#/.NET with Semantic Kernel: RAG over 52k recipes in Qdrant, a rules-first dietary validator, a meal planner, and five guardrail layers. It issues LLM calls from five places — query expansion, recipe reranking, dietary substitution, intent entity extraction, and general Q&A.

I moved inference to Token Factory to evaluate it as a backend for that workload. The system was previously on Groq (Llama 3.3 70B), which replaced local Ollama and produced a [21× latency improvement on the general-question path](../docs/adrs/012-cloud-deployment.md).

**What the port cost:** one new `NebiusProvider` class, one config switch, zero agent code changes. That's the fourth provider swap this architecture has absorbed without touching agent logic (HuggingFace → Nomic → Voyage for embeddings, Ollama → Groq → Nebius for LLM). Token Factory's API is OpenAI-compatible in the ways that matter: plain-string message content works, and existing request shapes port unchanged.

---

## Methodology, before the results

Stating this first because the negative result below only means something if the method is visible.

**Level 1 isolation.** The harness calls the provider directly — no Qdrant, no Redis, no embeddings. ChefAgent has a known ~3,000ms Upstash cold start and a Voyage free-tier RPM ceiling; either would dominate end-to-end numbers and get misattributed to inference.

**Real prompts, not synthetic ones.** The 46-query set is the five prompt shapes ChefAgent actually issues, lifted verbatim from the call sites and filled with content from the committed golden dataset. Input length spans 8.5× — 64 tokens for a general question, up to 371 for a reranker prompt carrying retrieved recipes. Benchmarking raw user messages would have measured prompts the system never sends.

**Interleaved rounds.** Five rounds, each running concurrency 1, 8, and 32 back to back in shuffled order, pooled by config afterward. An earlier sequential sweep produced concurrency 1 *slower* than concurrency 32, which is not a real result — it's what happens when endpoint drift lands entirely on whichever config runs last.

**Errors are data.** Failures are recorded as samples with status codes rather than thrown. This is how a wrong model ID showed up as 46 clean 404s instead of a stack trace.

**Prompt caching is measured, not defeated.** Token Factory caches by default and reports `cached_tokens`. Repeated prompts across configs get hits, so run order is shuffled under a fixed seed and cache rate is recorded per request.

690 requests, model `meta-llama/Llama-3.3-70B-Instruct`, 2026-08-25 15:58–17:02 UTC. Total cost: **$0.03**.

---

## What I could measure

### Generation is faster than advertised, and concurrency-independent

The endpoint page advertises 25 Tok/s. Measuring only the streaming phase — completion tokens over that request's own post-first-token duration:

| Concurrency | n | p50 tok/s |
|---|---|---|
| 1 | 230 | **43.3** |
| 8 | 230 | **41.5** |
| 32 | 230 | **39.1** |

A 1.1× spread across a 32× change in concurrency, at roughly 1.6× the published figure.

This is the most defensible number here because it's a within-request measurement. It never compares one block of time against another, so the session variance described below can't reach it.

### Structured output was perfect at n=690

Four of ChefAgent's five call sites demand strict JSON. Across 420 JSON-demanding responses spanning three concurrency levels: **zero parse failures, zero responses needing markdown-fence stripping.**

That second part matters more than it looks. ChefAgent's dietary validator carries a defensive `raw.Replace("```json", "")` — written because fences do occur in the existing setup. That code path never fired once.

Caveat: 420 samples would likely surface a 1-in-200 failure rate. It cannot exclude a rarer one.

### Cost

690 requests consumed 136,815 input tokens (46% served from cache) and 34,151 output tokens: **$0.05 per 1,000 requests** at $0.13/1M in and $0.40/1M out. That's an upper bound — I never established whether cached input bills at a reduced rate.

---

## What I couldn't measure, and why

**I set out to compare concurrency levels. The data doesn't support it.**

Round-to-round p50 time-to-first-token, *within a single config*:

| Config | Range across rounds | Spread |
|---|---|---|
| c=1 | 1,812 – 6,021 ms | 3.3× |
| c=8 | 298 – 7,352 ms | **24.7×** |
| c=32 | 2,319 – 12,695 ms | 5.5× |

Pooled difference *between* configs: under 2×.

Within-config noise exceeds between-config signal by an order of magnitude. Any ranking I published from this would be an artifact of which round each config happened to occupy.

More rounds wouldn't fix it. To check whether the variance was self-inflicted — my own sustained load degrading my own requests — I probed at 60-second intervals for 64 minutes with **nothing else running**:

| Band | n | Share |
|---|---|---|
| < 1s | 13 | 24.1% |
| 1–10s | 18 | 33.3% |
| 10–30s | 18 | 33.3% |
| **> 30s** | **5** | **9.3%** |

*54 probes, identical 10-token request. p50 5.71s, p95 33.61s, max 99.77s — a 175× spread at idle.*

Not self-inflicted, then. And not a degradation curve either: the distribution is bimodal. The fast mode is remarkably tight — thirteen samples spanning 0.57–0.67s — with the rest scattered from seconds to a minute and little in between. So ~0.6s is what the endpoint is capable of, delivered about a quarter of the time.

---

## Why the 9.3% is the finding

ChefAgent's cloud HTTP client is configured with a 30-second timeout. That was a [deliberate choice](../docs/adrs/012-cloud-deployment.md), documented as "30s failure = real problem."

Against this endpoint, roughly **one idle request in eleven crosses that threshold** — on a 10-token prompt, with no concurrency and no load.

A meal plan in ChefAgent is 14 sequential LLM calls. If those admission delays were independent, the chance of completing one without a single timeout would be about 0.907¹⁴ ≈ **25%**. I haven't tested independence, so treat that as an order-of-magnitude illustration rather than a measured rate — but the direction is not in doubt.

The port also surfaced a bug of my own that this makes urgent: the intent router wraps entity extraction in a 90-second cancellation token while the HTTP client caps at 30. The inner token can never fire. I'd logged that as a code smell; it's now a failure mode with a rate attached.

**So the shape of it:** admission is chaotic, generation is excellent, correctness is perfect. For throughput-bound work — long generations, batch jobs — you amortise one admission wait across many tokens and the token rate beats the spec sheet. For a latency-sensitive agent pipeline making many short sequential calls, you pay the admission lottery on every hop and the generation advantage never compounds. The tail also can't be tuned away by shortening prompts: past saturation, prompt length stopped predicting time-to-first-token entirely.

---

## Friction log

Seven things that cost me time, each with what I'd want instead.

**The model catalog page shows a `/v1/responses` example.** ChefAgent's provider interface takes a role-tagged message list, which the Responses shape would flatten. Someone porting OpenAI-compatible code lands on the catalog page first and is steered toward the surface that doesn't match their code. *Show the chat-completions snippet, or label which surface suits which case.*

**No `-fast` variants were reachable.** The product page presents fast vs base as a per-workload choice. `GET /v1/models` returned 30 models, none with a `-fast` suffix, and the ID I inferred returned 404 on all 46 requests. This says fast wasn't available *to me, on this endpoint, on this account* — not that it doesn't exist. But there's no way to find out from the API: model objects carry only `id`, `created`, `object`, `owned_by`. *Expose serving flavor in the model object, or document which models offer which.*

**`created` isn't a release date.** It reports a timestamp from this month for Llama 3.3 70B — deployment time, presumably. Anyone dating a model from that field would be wrong.

**No way to distinguish "my code is slow" from "the platform is slow right now."** Given a 175× idle spread, a first-time user has no signal to tell these apart. *A status page or a published latency distribution.*

**A single tok/s figure is the wrong summary statistic for this endpoint.** p50 tells a customer almost nothing here. The p90 and the rate at which requests cross a typical client timeout are what capacity planning actually needs.

**Cached-token billing is undocumented.** 46% of my input tokens were cache hits. Whether they bill at a reduced rate changes cost modelling materially, and I couldn't find out.

**The catalog page blocks automated access.** Minor, but it means you can't script against the docs.

To be clear about what went well: `stream_options.include_usage` is honored, so one pass yields time-to-first-token, latency, and token counts together. Prompt caching is on by default and reported per request. Zero 429s across 690 requests including bursts at concurrency 32. And the OpenAI compatibility held — the port was genuinely a config change.

---

## What I'd do next

- **Long-generation test** (`max_tokens` 2000+) to check the amortisation claim the recommendation rests on. That's the one assumption doing real work here.
- **Test whether admission delays are independent between sequential requests.** The 25% figure depends on it.
- **End-to-end through `/chat`** using the committed 50-case sweep, with the six known intent-router failures as a correctness control — they should fail identically on Nebius, and if any passes, something moved that shouldn't have.

---

## Reproducing this

```bash
export NEBIUS_API_KEY=...
dotnet run --project bench -- bench/queries.json bench/results/ 5
python3 bench/analyze.py bench/results/raw-<runId>.jsonl
```

Every run is kept, including the failed and inconclusive ones, labeled in [`bench/results/README.md`](./results/README.md). Deleting runs that didn't fit is what discredits benchmarks.

I'd genuinely like to be wrong about the admission variance — if it's an artifact of account tier, region, or something in my client I haven't spotted, I'd like to know. The harness is there to rerun.