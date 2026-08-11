---
name: verify-deploy
description: Confirm a push to main actually deployed and is serving the expected change. Use when asked "did my push deploy", "check prod", or "verify the deployment".
---

# Verify a deploy landed

There is no deploy *command* in this repo — Railway (API) and Vercel (frontend) auto-deploy on push to `main`, per `docs/adrs/012-cloud-deployment.md`. There's also no rollback procedure documented anywhere in this repo — if a bad deploy needs reverting, that's a Railway/Vercel-console action outside this codebase, not something this skill can automate. Flag that gap rather than improvising one.

## Steps

1. `curl -sf https://chefagent-production.up.railway.app/health` — confirms the process is up. Not sufficient on its own (see [[verify-local-stack]]'s point about lazy provider-config failures — the same risk applies in prod).
2. `curl https://chefagent-production.up.railway.app/` (`GET /`, `MapServiceInfo`) — reports configured providers by name. Confirm this matches what the pushed change actually set (e.g. if you changed `EmbeddingProvider`, confirm the response says the new provider, not the old one cached from a stale deploy).
3. One live `/chat` (or the specific endpoint the change touched) exercising the changed behavior directly — a passing health check proves the process runs, not that your change works.
4. If the change touched retrieval/eval-relevant behavior, note that eval numbers (56/60, RAGAS scores) are frozen snapshots as of specific commits (see [docs/CLAUDE.md](../../../docs/CLAUDE.md)) — a deploy verification is not the same as re-running eval, and a passing deploy doesn't mean those numbers are still accurate if retrieval behavior changed.

## Verification

Health 200 + stack info matches the pushed config + one live request behaves as intended, on the actual production URL, not a local instance.
