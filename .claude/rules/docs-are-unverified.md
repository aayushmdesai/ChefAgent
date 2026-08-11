# Docs are unverified

Never cite a claim from `docs/` (README.md, CHANGELOG.md, `docs/tech-debt.md`, `docs/adrs/*`, `docs/weeklyProgress/*`, `docs/*-retrospective.md`) as fact without checking the actual code first. This repo has a confirmed history of docs describing things that don't match reality.

**Why:** An August 2026 investigation cross-checked ~50 specific doc claims against the code and found concrete misses: README's ADR table links 9 of 13 filenames wrong (dead links), README claims "80+ tests" but the actual count is ~68 across only 4 test files, `docs/tech-debt.md` is dated "Week 16" but describes an item (`S-3`, query-expansion default) as still deferred when it was actually fixed in Week 18, and `CHANGELOG.md` stops at Week 12 despite seven more weeks of shipped work. The docs corpus itself admits further inaccuracies it caught late (e.g. a portfolio-site regression that was displayed as a gain due to mis-baselined deltas, caught only on a second audit pass). See `docs/CLAUDE.md` for the full per-file trust map.

**How to apply:** Before repeating a doc's claim about "what exists" or "what's fixed," grep or read the actual file. Treat `docs/` as a lead to verify, not a source of truth — this applies especially to anything a fresh session might be tempted to summarize back to the user as current state.
