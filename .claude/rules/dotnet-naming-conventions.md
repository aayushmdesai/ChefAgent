# .NET naming and file structure conventions

- Interfaces are `I`-prefixed (`ILlmProvider`, `IEmbeddingProvider`).
- Async methods are `Async`-suffixed, with zero exceptions found (`ChatAsync`, `EmbedAsync`, `SearchRecipesAsync`, `ClassifyAsync`).
- One class per file; filename matches the class name.
- Code is organized folder-per-concern under `src/shared/`: `Providers/Llm/`, `Providers/Embeddings/`, `Guardrails/`, `Observability/`. `RateLimiter.cs` and `SessionStore.cs` are the exceptions — they live at the top level of `src/shared/`, not in a subfolder, despite `RateLimiter` being guardrail-adjacent.
- Each agent (Recipe, Diet, Planner, Orchestrator) is its own `.csproj`/project under `src/agents/`, not a folder within one shared project.
- Files use `// ── Section Name ──────` banner-style comments to divide a file into logical regions, and XML `/// <summary>` doc-comments on public classes/methods that explain *why* a design choice was made (e.g. citing an FDA-labeling source in `DietaryRules.cs`), not just what the method does.

**Why:** These are the only patterns found consistently across every backend file sampled — not aspirational, actually followed.

**How to apply:** Match these conventions for any new class, file, or folder placement in `src/`. When adding a doc-comment, prefer explaining a non-obvious rationale over restating the method name in prose.
