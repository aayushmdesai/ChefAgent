---
name: add-llm-or-embedding-provider
description: Add a new LLM or embedding provider (a new API/model backend) to the ChefAgent .NET backend. Use when asked to "add support for provider X", "wire up a new LLM/embedding provider", or "add another model option" for ChefAgent's chat or embedding path.
---

# Add an LLM or embedding provider

ChefAgent's backend already supports 2 LLM providers (Groq, Ollama) and 4 embedding providers (Voyage, Nomic, HuggingFace, Ollama), all following one identical pattern. Adding another means extending that pattern, not inventing a new one.

## Steps

1. **Create the provider class** under `src/shared/Providers/Llm/` (implementing `ILlmProvider`, i.e. `Task<string> ChatAsync(IEnumerable<ChatMessage> messages, CancellationToken ct = default)` + a `ModelName` property) or `src/shared/Providers/Embeddings/` (implementing `IEmbeddingProvider`, i.e. `Task<float[]> EmbedAsync(string text, CancellationToken ct = default)`). Match the constructor shape of an existing provider in the same folder (e.g. `VoyageEmbeddingProvider.cs` or `GroqProvider.cs`) — they take an `HttpClient`, an API key string, and a model name string.

2. **Register it in `src/api/ServiceRegistration.cs`**, inside `AddInfrastructure`. Find the existing `services.AddSingleton<ILlmProvider>(sp => {...})` or `services.AddSingleton<IEmbeddingProvider>(sp => {...})` factory lambda and add a new `if (providerName == "x") { ... }` branch before the final fallback. Copy the shape exactly from a neighboring branch:
   ```csharp
   if (embeddingProvider == "x")
   {
       var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Cloud");
       var apiKey = config["X:ApiKey"]
           ?? throw new InvalidOperationException("X:ApiKey required when EmbeddingProvider=x");
       var model = config["X:Model"] ?? "default-model-name";
       return new XEmbeddingProvider(httpClient, apiKey, model);
   }
   ```
   The `"Cloud"` named `HttpClient` (30s timeout) is already registered for any cloud provider — don't create a new named client unless the provider needs a materially different timeout.

3. **Add the env vars to `docker-compose.yml`**, not `.env.example` — `.env.example` is already stale and missing `LlmProvider`/`EmbeddingProvider`/every existing cloud API key, so it isn't the source of truth for what the app actually reads. Follow the `docker-compose.yml` naming convention already in use: `X__ApiKey=${X_API_KEY}` (double-underscore for the nested config key, matching ASP.NET Core's config-binding convention).

4. **Add an `appsettings.json` section only if the provider needs config beyond ApiKey/Model** (most don't — compare against the existing `Qdrant`/`Ollama` sections for the pattern if one is needed).

5. Selection is switched via the top-level `LlmProvider` or `EmbeddingProvider` config value (`groq`/`ollama`, or `voyage`/`nomic`/`huggingface`/`ollama`) — no code change needed elsewhere once the branch exists; verify by setting the env var and hitting `GET /` (`MapServiceInfo` in `Endpoints.cs`), which reports the configured embedding provider back by name.

See [[dotnet-di-pattern]] for the full registration convention this follows.
