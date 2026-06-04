namespace ChefAgent.Shared;

/// <summary>
/// Abstraction over any text embedding provider (Ollama, HuggingFace, etc.)
/// Callers pass raw text; the provider handles prefixes, batching, and API details.
/// </summary>
public interface IEmbeddingProvider
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}
