# Week 10 Observability Test Summary

**Run date:** 2026-06-02 20:37 UTC  
**API:** http://localhost:5100  
**Langfuse:** http://localhost:3100

## Test Results

| # | Label | Session | Intent | Latency | Status |
|---|-------|---------|--------|---------|--------|
| 1 | quick chicken dinner | obs-basic-1 | SearchRecipe | 879ms | ok |
| 2 | dairy-free pasta | obs-dairy-1 | SearchRecipe | 125ms | ok |
| 3 | nut-free pasta (merged with dairy-free) | obs-dairy-1 | SearchRecipe | 69ms | ok |
| 4 | broiling vs baking | obs-general-1 | GeneralQuestion | 9794ms | ok |
| 5 | plan dinners for the week (dairy-free) | obs-plan-1 | CreateMealPlan | 639ms | ok |
| 6 | get meal plan | obs-plan-1 | GetMealPlan | 16ms | ok |
| 7 | swap Monday dinner to pasta | obs-plan-1 | ModifyMealPlan | 118ms | ok |
| 8 | injection attempt | obs-inject-1 | Unknown | 4ms | ok |
| 9 | repeated query attempt 1 | obs-repeat-1 | SearchRecipe | 85ms | ok |
| 10 | repeated query attempt 2 | obs-repeat-1 | SearchRecipe | 63ms | ok |
| 11 | repeated query attempt 3 | obs-repeat-1 | Unknown | 3ms | ok |
| 12 | is pasta carbonara dairy-free? | obs-diet-1 | ValidateDiet | 99ms | ok |
| 13 | lactose intolerant soup (LLM extraction) | obs-extract-1 | SearchRecipe | 14330ms | ok |

## Metrics Snapshot (last 5 min)

| Metric | Value |
|--------|-------|
| Total requests | 13 |
| Completed | 11 |
| Blocked | 2 |
| p50 latency | 101ms |
| p95 latency | 14326ms |
| p99 latency | 14326ms |
| Min latency | 12ms |
| Max latency | 14326ms |

### Requests by Intent

| Intent | Count |
|--------|-------|
| SearchRecipe | 6 |
| GeneralQuestion | 1 |
| CreateMealPlan | 1 |
| GetMealPlan | 1 |
| ModifyMealPlan | 1 |
| ValidateDiet | 1 |

## Portfolio Screenshots

Take manually from http://localhost:3100 → Traces:

| File | Session | What to show |
|------|---------|--------------|
| `trace_search_dairy_free.png` | obs-dairy-1 | Timeline: chat → orchestrator → recipe_agent.search → diet_agent.validate x5 |
| `trace_meal_plan_generation.png` | obs-plan-1 | Timeline: planner_agent.generate with 7 day branches |
| `trace_injection_blocked.png` | obs-inject-1 | Short trace, no agent spans |

## Overhead Assessment

Tracing overhead is < 1ms per request. Evidence:
- `StartSpan` writes to `Channel<T>` and returns (nanoseconds)
- Background worker POSTs to Langfuse asynchronously (199ms, 47ms — invisible to request thread)
- p50 of 101ms reflects Ollama embedding cost, not tracing overhead
- Fire-and-forget design confirmed working as intended