# The agentification layer — what's here and how to extend it

This directory (plus the `CLAUDE.md` files scattered through the repo) is a knowledge/scaffolding layer for AI coding agents working on ChefAgent. Built in an August 2026 audit pass that treated every existing doc claim as unverified until checked against actual code — see `docs/CLAUDE.md` for why that stance exists. Nothing here changes application behavior; it's pure orientation and process.

## Memory (`CLAUDE.md` files, root + 6 nested)

Loaded automatically at session start. Root `CLAUDE.md` orients and delegates — it should stay short enough to scan and point down, not accumulate area-specific detail. The 6 nested files (`src/`, `src/agents/`, `src/frontend/`, `eval/`, `scripts/`, `docs/`) carry the area-specific facts: file lists, exact defaults, specific known bugs. **Extend when**: you find a new verified fact worth remembering (a footgun, a convention, a file's real purpose) — add it to the nested file that owns that area, not root, unless it's genuinely cross-cutting. Every claim in these files should be traceable to a specific `grep`/`find`/`Read` result, not inferred from a doc's prose — that's the standard the whole layer is held to, and the standard that caught the ADR-link/test-count/`Program.cs`-try/catch errors documented in the git history of this pass.

## Rules (`.claude/rules/*.md`)

Five files, each a coding convention this codebase actually and consistently follows (DI wiring, structured logging, naming, resilience pattern, plus the meta-rule that docs need verification). Loaded as standing context — not invoked, always active. **Extend when**: you notice a pattern followed with zero exceptions across multiple files, not just once. **Don't add a rule for**: something followed inconsistently, or something you'd like the codebase to do but it doesn't yet — that's aspirational, and this layer's whole point is not making that mistake. If a rule stops matching the code (a refactor changes the pattern), fix or delete it in the same change that breaks it, not later.

## Skills (`.claude/skills/*/SKILL.md`)

Ten repeatable, multi-step, repo-specific procedures — invoked when their `description` frontmatter matches what's being asked (Claude decides this automatically based on the trigger phrasing in the description). Each one ends in a verification step by design — a skill that ends in an assumption isn't finished. **Extend when**: you catch yourself explaining the same multi-step procedure for the second time, or a skill's steps stop matching reality (e.g. a file gets moved, a default flips) — fix it immediately, in the same session that discovers the drift, following this project's own "verify before writing" rule from `write-progress-doc`.

## Commands (`.claude/commands/*.md`)

Three slash-command entry points (`/start-session`, `/close-day`, `/regression-check`) for workflows run repeatedly by the user, not just referenced by an agent mid-task. Thinner than skills — they mostly sequence existing skills/agents rather than containing their own procedural detail. **Extend when**: a specific sequence gets run by name often enough that giving it a slash command saves real typing/context — not for one-off procedures, that's what skills are for.

## Agents (`.claude/agents/*.md`)

Two, deliberately kept few: `done-verifier` (independently re-checks "done" claims in a fresh context window — this repo's single most justified addition, given its demonstrated history of claiming things were built that weren't, on both branches) and `rules-reviewer` (checks a diff against the 5 rule files specifically). **Extend when**: a review/verification task needs a genuinely separate context window to be trustworthy — i.e. the same session that did the work is the wrong one to grade it. Don't add an agent for something a skill or command already covers; a fresh context window is the expensive resource here, spend it deliberately.

## `.claude/settings.json`

Permission allowlist for read-only/low-risk commands the skills and commands above actually invoke, plus three hooks:
- `SessionStart` — surfaces current branch + last commit as context, cheap (~one `git` call), fired once. Exists specifically because a fresh session with no branch awareness produced a materially wrong "the project is paused" conclusion during this audit — `main` and `phase-2` tell very different stories.
- `PostToolUse` (×2) — eslint on frontend `.jsx` edits, `dotnet build` compile-check on backend `.cs` edits. Fast, per-edit, non-blocking (`|| true`).
- `Stop` — a final `dotnet build` gate before a turn ends, but only if `.cs` files are actually dirty (checked via `git status --porcelain` first, so a docs-only turn doesn't pay for a solution build). Warns via `systemMessage`, doesn't block — deliberately, per the "hooks stay cheap, slow checks go in commands" split.

**Extend when**: a new check is fast (sub-few-seconds) and unconditionally useful on every matching edit. **Don't add a hook for**: anything needing containers, network calls, or the full test suite — those belong in `/close-day` or `/regression-check`, which the user invokes deliberately rather than paying the cost on every edit.

## `.mcp.json`

Declares `mcp-dotnet-diagnostics` (the user's own diagnostics tool, unrelated to ChefAgent's application code — see the "Related" section of the root `README.md`) as a project-level MCP server, so it's available to anyone working in this repo rather than depending on per-user global config. **Extend when**: a server provides real, repo-specific leverage — evaluated and explicitly rejected during this pass: a Postgres MCP for Langfuse's backing DB (its own UI already covers this), a Redis MCP (existing endpoints cover it), a Qdrant-specific MCP (the app itself is the client; REST + dashboard cover inspection). Don't pad this file with generic servers that don't have a concrete use case tied to this specific codebase.

## The one meta-rule underneath all of it

This whole layer exists because this project has a demonstrated, repeated pattern — on *both* branches — of documentation claiming something was built, fixed, or verified when a plain `find`/`grep` shows otherwise. Every piece above is designed around that fact: memory files cite their verification, rules are grounded in cited code, skills end in checks, and the `done-verifier` agent exists specifically to catch this failure mode before it compounds. If you're extending any piece of this layer, hold your own addition to the same standard — an unverified claim added here is exactly the mistake this layer was built to stop making.
