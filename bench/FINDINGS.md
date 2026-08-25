# Token Factory — Findings Log

Running notes from porting ChefAgent's inference to Nebius Token Factory and
benchmarking it.

**Rule for this file:** every entry is tagged VERIFIED (observed directly, with
the command or run that produced it) or HYPOTHESIS (a proposed explanation, not
yet tested). Nothing gets promoted from HYPOTHESIS to VERIFIED without a test
named in this file. The writeup only draws on VERIFIED entries.

---

## Session 1 — 2026-08-24 (evening, ~21:00–22:00 UTC)

### Environment

- Model: `meta-llama/Llama-3.3-70B-Instruct`
- Endpoint: `https://api.tokenfactory.nebius.com/v1/chat/completions`
- Client: .NET 8, custom `NebiusProvider`, streaming with `stream_options.include_usage`
- Harness: `bench/` console project, Level 1 (no Qdrant / Redis / Voyage)
- Query set: 46 prompts lifted verbatim from ChefAgent's five LLM call sites
- Endpoint page (2026-08-24): **$0.13 / 1M input, $0.40 / 1M output, 25 Tok/s**

---

## API surface

### F-01 — Endpoint is `/v1/chat/completions`, not `/v1/responses` — VERIFIED

The model catalog page presents a `/v1/responses` curl as the default snippet.
The docs elsewhere show the OpenAI SDK pointed at chat completions. A developer
porting existing OpenAI-compatible code lands on the catalog page first and is
steered toward the surface that does *not* match their code.

Chose chat completions: ChefAgent's `ILlmProvider` takes a role-tagged message
list, which Responses' `input` + `instructions` shape would flatten lossily.

*Customer ask: catalog page should show the chat-completions snippet, or label
which surface suits which use case.*

### F-02 — Plain-string message content works — VERIFIED

Catalog example uses the content-array form (`content: [{type:"text",...}]`) for
the user message but a plain string for the system message. Plain strings work
for both. Existing OpenAI-compatible code ports without changes.

### F-03 — `stream_options.include_usage` is honored — VERIFIED

Final SSE frame carries `usage` with an empty `choices` array. One pass yields
TTFT, total latency, and token counts together — no two-pass fallback needed.

### F-04 — Prompt caching is on by default and reported — VERIFIED

`usage.prompt_tokens_details.cached_tokens` present on both streaming and
non-streaming responses. Observed 16 cached tokens on a 38-token prompt with no
shared prefix authored by me — that is the chat template being cached, not user
content. Short prompts therefore show a misleadingly high cache rate.

**Consequence for methodology:** repeated prompts across configs get cache hits.
Run order is shuffled under a fixed seed (20260824) so cache warmth cannot
correlate with concurrency level. Cache rate is recorded per request.

*Open: are cached input tokens billed at a reduced rate? Not yet checked.*

### F-05 — Response carries vLLM-flavored fields — VERIFIED

`prompt_token_ids`, `stop_reason`, `kv_transfer_params`, `prompt_logprobs` —
outside the OpenAI spec. Harmless when deserializing selectively. Detail objects
can be null (`completion_tokens_details: null` observed), so every hop needs
guarding.

*Suggests the serving stack is vLLM-based. Stated as an inference, not a fact.*

### F-06 — No `-fast` variants exposed on this account — VERIFIED

`GET /v1/models` returned 30 models, **zero** ending in `-fast`. Attempting
`meta-llama/Llama-3.3-70B-Instruct-fast` returned 404 on all 46 requests
(run `20260824-213426`):

```
{"detail":"The model `meta-llama/Llama-3.3-70B-Instruct-fast` does not exist."}
```

The product page presents fast vs base as a per-workload choice. Model objects
carry only `id`, `created`, `object`, `owned_by` — no flavor or tier field, so
the distinction is not discoverable through the API.

**Careful wording for the writeup:** this says fast variants were not available
*to me, on this endpoint, on this account*. It does not establish that they do
not exist. They may sit behind dedicated endpoints, a paid tier, or different
IDs.

*Customer ask: expose serving flavor in the model object, or document which
models offer which flavors.*

### F-07 — `created` is not a release date — VERIFIED

`meta-llama/Llama-3.3-70B-Instruct` reports `created: 1787607375` — a timestamp
from this month, not the model's release. Probably deployment time. Anyone using
this field to date a model would be wrong.

---

## Performance

### F-08 — JSON parse rate was 100% across all configs — VERIFIED

Run `20260824-215404`. 28 of 46 prompts demand strict JSON (RecipeReranker,
IntentRouter entity extraction, DietValidation substitutions). Across three
concurrency levels — 84 JSON responses total — **zero parse failures and zero
responses needing markdown-fence stripping.**

This matters because ChefAgent's `DietValidationPlugin` already carries a
defensive `raw.Replace("```json","")`, implying fences do occur in the current
setup. They did not occur here.

*Not affected by the latency problems below — correctness is not time-sensitive.
This is currently the most solid quantitative finding.*

*Caveat: 84 samples. A 1-in-100 failure rate would plausibly show as zero.*

### F-09 — Single-request latency varies ~15× at zero concurrency — VERIFIED

Five identical `"Say OK"` requests (10 max_tokens), 20s apart, nothing else
running:

```
4.75s   5.68s   13.74s   19.29s   70.91s
```

This is the endpoint's behavior with **no load from me at all**. Not throttling
(throttling would be consistently slow), not a transient outage (values recover
and re-degrade).

**This invalidates the interpretation of every cross-config comparison made
tonight**, and it is arguably the most useful finding for a customer: p50 tells
you almost nothing here. An agent pipeline making 14 sequential LLM calls per
meal plan inherits this variance 14 times.

### F-10 — TTFT clusters into discrete waves at c=32 — HYPOTHESIS

Run `20260824-214822`, c=32, sorted TTFT (ms):

```
9721, 9722, 9725      (3 ms apart)
14866, 14871, 14875   (9 ms apart)
18074, 18075, 18075   (1 ms apart)
16179, 16179          (0 ms apart)
```

Independent requests do not agree to within 1 ms. Proposed mechanism: continuous
batching on the serving side admitting requests in discrete groups — consistent
with F-05's vLLM signals.

**Why this is still a hypothesis:** given F-09's baseline variance, clustering
this tight is hard to explain by chance — but it has been observed in a single
run, during a window whose overall health is unknown. Needs repetition across
several independent runs before it can be claimed.

*Test to run: c=32 config repeated 5+ times spread over hours, each preceded by
a logged health probe. Wave structure present in all → structural.*

### F-11 — Prompt length predicts TTFT until saturation, then stops — HYPOTHESIS

Same run. The first ~15 requests order cleanly by prompt size (GeneralQuestion
67 tok @ 241 ms → RecipeReranker 346 tok @ 1923 ms). Past ~2.5 s everything
collapses into the F-10 waves and prompt size predicts nothing: two
GeneralQuestion prompts of 67 and 69 tokens landed at 241 ms and 21,872 ms.

If it holds, the practical consequence is that **shortening prompts would not
move the tail** — admission order dominates, not prefill.

*Same test as F-10.*

### F-12 — Generation throughput beats the advertised rate — VERIFIED

The endpoint page advertises **25 Tok/s**. Observed generation rate, computed as
`CompletionTokens / ((TotalMs - TtftMs) / 1000)` — i.e. excluding time-to-first-
token, measuring only the streaming phase — from run `20260824-215404`:

| Concurrency | n | p50 tok/s | p95 tok/s | min tok/s |
|---|---|---|---|---|
| 1 | 46 | **37.87** | 52.91 | 0.67 |
| 8 | 45 | **43.01** | 58.99 | 0.61 |
| 32 | 34 | 2.93 | 51.57 | 1.51 |

At c=1 and c=8, generation runs **~1.5–1.7× faster than advertised**. This is a
favorable finding and it is the most defensible performance number collected so
far, because it is a within-request measurement — it does not depend on
comparing across configs, so F-09's session variance does not confound it.

**Filter applied:** requests with `CompletionTokens < 20` or a streaming phase
under 200 ms are excluded. Without the filter the arithmetic produces absurd
values (214,843 tok/s) because short responses arrive in a single SSE frame and
the denominator collapses toward zero. n drops from 46 to 34 at c=32, meaning
**12 of 46 responses arrived essentially in one frame at high concurrency** —
worth checking whether those cluster inside the F-10 waves.

### F-13 — Generation appears to collapse at c=32, bimodally — HYPOTHESIS

Same run. c=32 p50 falls to 2.93 tok/s — a ~15× drop from c=8 — while the c=32
**p95 stays at 51.6 tok/s**, in line with the other configs. So it is not a
uniform slowdown: at high concurrency some requests stream at full speed while
the median trickles. That bimodal shape is consistent with F-10's waves —
requests admitted into an active batch stream normally, requests waiting between
waves do not.

**Why this stays a hypothesis:** c=32 ran *first* in this sweep and c=1 ran
*last*, inside the session that produced F-09's 15× variance. The confound
points the opposite way from the c=1 case but is the same confound. Cannot
separate a real concurrency effect from session drift without interleaved runs.

*Test to run: interleaved rounds pooled by config, per the next-session plan.*

### F-14 — Cost is negligible at this scale — VERIFIED

Run `20260824-215404` consumed 27,363 input and 6,824 output tokens across 138
requests. At the posted $0.13/1M in and $0.40/1M out, that is roughly
**$0.0063** — under a cent for a full three-config sweep.

**Consequence:** cost is not a constraint on methodology. Repetition, interleaved
sweeps, and large variance baselines are all affordable. There is no budget
argument for undersampling.

*Still open: whether the 8,752 cached input tokens bill at a reduced rate. If
they do, actual spend is lower still. Does not change the conclusion.*

---

## Session 2 — 2026-08-25 (15:58–17:02 UTC)

Run `20260825-155808`. Interleaved design: 5 rounds, each running c=1 / c=8 /
c=32 in shuffled order, health probe logged at round start and end. 690 requests
total, 46-query set unchanged. Analysis pools by config across rounds.

**690/690 succeeded. 690/690 parsed. Zero errors, zero 429s.**

### F-15 — Concurrency comparison is NOT POSSIBLE with this data — VERIFIED

The most important result of the session, and a negative one.

Round-to-round p50 TTFT spread *within a single config*:

| Config | p50 range across rounds | Spread |
|---|---|---|
| c=1 | 1,812 – 6,021 ms | 3.3× |
| c=8 | **298 – 7,352 ms** | **24.7×** |
| c=32 | 2,319 – 12,695 ms | 5.5× |

Pooled difference *between* configs: 4,687 / 5,527 / 7,806 ms — under 2×.

**Within-config noise exceeds between-config signal by an order of magnitude.**
Any statement of the form "concurrency N is faster than concurrency M" is
unsupported by this dataset. The pooled table looks like it ranks the configs;
it does not.

Interleaving did its job — it made the noise visible instead of silently
assigning it to whichever config ran last (the Session 1 error). It did not
reduce the noise, and more rounds would not: the variance is structural, not
sampling error.

*This supersedes any concurrency claim implied by Session 1.*

### F-16 — Generation rate is stable and concurrency-independent — VERIFIED

Against the 25 Tok/s advertised on the endpoint page:

| Config | n | gen p50 tok/s |
|---|---|---|
| c=1 | 230 | **43.3** |
| c=8 | 230 | **41.5** |
| c=32 | 230 | **39.1** |

**1.1× spread across a 32× change in concurrency**, while TTFT for the same
requests swung up to 25×. Generation runs ~1.6× faster than advertised and is
essentially indifferent to load.

This is the strongest number in the project. It is a *within-request*
measurement — completion tokens over that request's own streaming duration — so
it never depends on comparing across configs and F-15's noise cannot reach it.

*Supersedes F-13. The apparent c=32 generation collapse in Session 1 (2.93
tok/s) does not reproduce; it was session drift, exactly as the hypothesis
warned.*

### F-17 — Parse rate 100% at n=690, zero markdown fences — VERIFIED

420 JSON-demanding responses across five rounds and three concurrency levels.
**Zero parse failures. Zero responses needing fence-stripping.**

ChefAgent's `DietValidationPlugin` carries a defensive
`raw.Replace("```json","")` implying fences occur in the existing Groq setup.
That code path never fired once here.

*Upgrades F-08 from 84 samples to 690. At this n, a 1-in-200 failure rate would
likely have appeared. Still cannot exclude a rarer one.*

### F-18 — Health probes swing 100× within one hour — VERIFIED

Uncontended `"Say OK"` probes, logged at each round boundary:

```
r1 start 22,825 ms    r1 end 10,793 ms
r2 start 16,040 ms    r2 end  9,079 ms
r3 start 15,350 ms    r3 end     228 ms
r4 start  8,374 ms    r4 end     344 ms
r5 start    227 ms    r5 end   9,291 ms
```

Range 227 ms – 22,825 ms on an identical 10-token request. Immediately before
the run, five manual probes returned 0.46–0.58 s (spread 1.26×).

*This reframes F-09. The finding is not "the endpoint has 15× variance" but
"admission latency varies enormously and unpredictably over minutes." The
Session 1 evening window and the Session 2 pre-run window differ by ~40×.*

### F-19 — Variance is NOT self-inflicted — RESOLVED, hypothesis rejected

Tested by probing at ~60 s intervals for 64 minutes with **no benchmark
running** (`bench/results/idle-probes.txt`, 2026-08-25 17:42–18:46 UTC, n=54).

Result: 0.57 s – 99.77 s, a **175× spread at idle**. The variance is present
with zero load from this client, so it is not caused by my own traffic.

*The noisy-neighbour hypothesis is rejected. The latency findings describe the
platform, not the harness. This also means F-15's noise cannot be engineered
away by pacing the benchmark — it is a property of the endpoint.*

### F-22 — Idle latency is bimodal, and 9.3% of requests exceed 30 s — VERIFIED

Same 54-probe idle dataset. Identical 10-token `"Say OK"` request each time.

| Band | n | Share |
|---|---|---|
| < 1 s ("fast mode") | 13 | 24.1% |
| 1–10 s | 18 | 33.3% |
| 10–30 s | 18 | 33.3% |
| **> 30 s** | **5** | **9.3%** |

p50 5.71 s · p90 28.91 s · p95 33.61 s · p99 81.67 s · max 99.77 s

**Two observations that matter more than the percentiles:**

1. **The distribution is bimodal, not a degradation curve.** The fast mode is
   remarkably tight — 13 samples spanning 0.57–0.67 s. Requests either hit a
   warm path and return in ~0.6 s, or land somewhere else entirely and take
   seconds to a minute. There is no smooth middle. So ~0.6 s is what the
   endpoint is *capable* of, delivered about a quarter of the time.

2. **ChefAgent's `"Cloud"` HttpClient is configured at a 30 s timeout.**
   Therefore **roughly 1 idle request in 11 would have failed outright in the
   production configuration** — on a 10-token prompt, with no concurrency and no
   load.

This converges with B-02: `IntentRouter` wraps entity extraction in a 90 s
`CancellationTokenSource` that can never fire because the client caps at 30 s.
That was logged as a code smell yesterday. It is now a measured production
failure mode with a rate attached.

*Customer ask: publish expected latency distribution, not a single tok/s figure.
A p50 tells a customer almost nothing about this endpoint; the p90 and the
timeout-crossing rate are what capacity planning actually needs.*

### F-20 — Cache rate differs by config, confounding TTFT further — VERIFIED

Cached share of input tokens: c=1 35.4%, c=8 42.5%, c=32 59.3%. Overall 62,576
of 136,815 input tokens (46%) were cache hits.

The configs were not doing identical work. Does not touch F-16 (generation
measurement is post-prefill), but it is a further independent reason the TTFT
comparison in F-15 cannot be rescued.

### F-21 — Cost confirmed at scale — VERIFIED

690 requests: 136,815 input (62,576 cached), 34,151 output tokens = **$0.0314**,
or **$0.05 per 1,000 requests** at posted rates.

*Assumes cached input bills at full rate — still unverified, so this is an upper
bound.*

---

## The shape of the story

Updated after Session 2. Interpretation, held separately from the findings.

**Admission is chaotic. Generation is excellent. Correctness is perfect.**

- **Admission (TTFT)** — bimodal and platform-side. At idle, 24% of requests
  return in ~0.6 s and 9.3% exceed 30 s; range 175× (F-22). Not caused by my own
  load (F-19). Round-to-round noise inside one config reaches 24.7×, swamping
  any between-config difference (F-15), so concurrency effects are not
  measurable at this sample size.
- **Generation** — 39–43 tok/s, ~1.6× the advertised rate, effectively
  independent of concurrency (F-16). Stable, favorable, well-evidenced.
- **Correctness** — 690/690 parsed, zero fences, zero errors, zero 429s (F-17).

**The customer-facing consequence**, which is the point of the whole exercise:

- **Throughput-bound work** — long generations, batch jobs, offline pipelines —
  is served well. You amortise one admission wait across many tokens, and the
  token rate beats the spec sheet.
- **Latency-sensitive agent pipelines are the bad case.** ChefAgent issues 14
  short sequential LLM calls per meal plan. Each pays the admission lottery
  independently, and the generation advantage never compounds because the
  generations are short. Median user-visible latency is therefore governed by a
  quantity that varies 175× and cannot be tuned by shortening prompts (F-11).
- **The concrete failure, not a hypothetical one:** at a 30 s client timeout —
  ChefAgent's actual production setting — 9.3% of idle requests would have failed
  (F-22). Across 14 sequential calls, the probability that a meal plan completes
  without a single timeout is roughly 0.907^14 ≈ **25%**. Three plans in four
  would fail somewhere. That arithmetic assumes independence between calls, which
  is untested, so treat it as an order-of-magnitude illustration rather than a
  measured rate.

That second bullet is the one to lead with. It follows from measurements, it is
specific to a workload shape rather than a vague verdict, and it is the actual
question an SA gets asked.

**What this artifact honestly cannot say:** which concurrency level is fastest,
whether the F-10 admission waves are structural, or whether the variance is the
platform's or self-inflicted (F-19). Publishing those as open questions with the
evidence attached is the correct move; publishing a ranking would be inventing a
result the data does not support.

---

## Bugs found in ChefAgent (mine, not Nebius's)

### B-01 — Shared `HttpClient` mutation in providers — FIXED

`GroqProvider` and the first `NebiusProvider` draft set
`DefaultRequestHeaders.Authorization` on the DI-provided named `"Cloud"` client,
which `VoyageEmbeddingProvider` also uses. Whichever constructs last wins —
Voyage calls could go out carrying a Nebius bearer token. Masked today only
because construction order happens to work.

Fixed in `NebiusProvider` by setting auth per request. **`GroqProvider` still
has this bug.**

### B-02 — Unreachable 90s timeout in IntentRouter — OPEN

`IntentRouter` wraps LLM entity extraction in a `CancellationTokenSource` of 90
seconds, but the `"Cloud"` HttpClient is configured at 30s. The inner token can
never fire. Given F-09, requests genuinely can exceed 30s, so this silently
converts a slow extraction into a timeout error.

### B-03 — `max_tokens: 512` hardcoded for every call site — OPEN

All five prompt shapes share one cap. Not observed to truncate in run
`20260824-215404` (parse rate 100%, so reranker JSON completed), but the
reranker returning many ranked recipes is the plausible case where it would.

---

## Methodology decisions and why

- **Level 1 isolates the provider.** No Qdrant, Redis, or Voyage in the harness.
  ChefAgent's known Upstash cold start (~3,000 ms first Redis call) and Voyage
  free-tier RPM ceiling would otherwise dominate and be misattributed to Nebius.
- **Prompts are the real ones.** Lifted verbatim from the five call sites, filled
  with content from the committed golden dataset and e2e sweep. Benchmarking raw
  user messages would measure prompts the system never sends.
- **Errors are recorded as data, never thrown.** A failure at c=32 becomes a
  sample with a status code, not a lost request. This is how F-06 was caught
  cleanly.
- **429s are a first-class column.** ChefAgent's `GroqProvider` absorbs rate
  limits into latency via retry; that would make throttling look like slowness.
- **No cost constants in code.** Rates change; token counts go in the raw file
  and cost is computed at analysis time.
- **Failed and inconclusive runs are kept**, labeled in
  `bench/results/README.md`. Deleting runs that did not fit is what discredits
  benchmarks.

---

## Open questions

1. Are cached input tokens billed at a reduced rate? (F-21 is an upper bound
   until this is settled. 46% of input tokens were cache hits, so it matters.)
2. ~~Per-token rates~~ — **collected**: $0.13/1M in, $0.40/1M out.
3. ~~Does F-10's wave structure survive repetition?~~ — **not answerable.** F-15
   shows TTFT noise is too large to resolve structure at this sample size.
4. ~~Is F-09's variance time-of-day dependent?~~ — **partly answered.** F-18
   shows 100× swings within a single hour, so it is not a simple diurnal
   pattern.
5. Do `-fast` variants exist anywhere on the platform, or only in the docs?
6. ~~Does F-13's c=32 generation collapse survive interleaving?~~ — **no.** F-16
   supersedes it. Session 1 artifact.
7. Do the 12 single-frame responses at c=32 fall inside the F-10 waves?
8. What does the advertised 25 Tok/s describe? Observed 39–43 tok/s across 690
   requests at every concurrency level (F-16). A floor, or a different flavor?
9. ~~Is the variance self-inflicted?~~ — **resolved, no.** F-19 rejected by
   64 minutes of idle probing showing 175× spread with zero load.
10. Would a longer-generation workload (max_tokens 2000+) show the admission cost
    amortising as predicted? Direct test of the central recommendation.
11. **Is the F-22 fast mode (~0.6 s, 24% of requests) a cache-warm path, a
    lightly-loaded replica, or routing?** Determines whether a customer can
    increase their share of it.
12. Are admission delays independent between sequential requests? The 25%
    meal-plan success estimate assumes so, and that assumption is untested.

---

## What I got wrong

Kept deliberately — the corrections are more instructive than the conclusions.

- **Assumed `-fast` existed** from Nebius's naming convention without checking
  `/v1/models` first. Cost one wasted config. Check the catalog, don't infer it.
- **Diagnosed "the endpoint is degraded" from a single 13.9 s probe**, and told
  myself to stop for the night — after having criticised a 46-sample p95 as
  noisy. Five probes showed variance, not degradation, and the correct response
  was more samples, not fewer. Drawing a strong conclusion from n=1 while
  warning about n=46 is the exact error the benchmark exists to avoid.
- **Computed tok/s without guarding the denominator.** First pass produced
  214,843 tok/s at c=32 — obviously impossible, which is the only reason it got
  caught. Responses arriving in a single SSE frame make `TotalMs - TtftMs`
  approach zero. Any rate metric needs a floor on both numerator and
  denominator, and an implausible result should be treated as a bug in the
  measurement before it is treated as a finding.
- **Ran configs sequentially within one sweep.** With F-09's variance, whichever
  config runs last carries whatever the endpoint was doing then. c=1 ran last and
  came out slower than c=32, which is not a real result. Fix: interleave rounds
  of all three levels, repeat, pool by config.

---

## Session 2 lessons

- **Interleaving revealed the noise; it did not remove it.** The design was
  correct and the outcome was still a negative result. That is the design
  working — Session 1's sequential sweep would have reported a confident,
  wrong concurrency ranking from the same underlying variance.
- **The stability check earned its place.** Printing per-round p50 alongside the
  pooled table is what made F-15 visible. A summary that only prints pooled
  figures lets an untrustworthy number look authoritative.
- **Within-request metrics survive session drift; cross-config metrics do not.**
  Generation rate (F-16) is solid at n=690 while TTFT comparison is unusable at
  the same n, purely because one compares a request against itself and the other
  compares blocks of time against each other. Worth designing for deliberately.

---

## Next session

1. ~~Settle F-19~~ — **done**, rejected. Variance is platform-side.
2. Long-generation config (`max_tokens` 2000+) to test the amortisation claim
   that the recommendation rests on.
3. Settle whether cached input tokens bill at a reduced rate.
4. Level 2 end-to-end through `/chat`, using the committed 50-case e2e sweep,
   with the six known IntentRouter failures as the correctness control — they
   must fail identically on Nebius.
5. Fix B-02: align the `IntentRouter` inner timeout with the client timeout, and
   raise the client timeout above 30 s given F-22.