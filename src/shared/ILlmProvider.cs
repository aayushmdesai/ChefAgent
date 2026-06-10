namespace ChefAgent.Shared;

/// <summary>
/// Abstraction over any LLM chat provider (Ollama, Groq, etc.)
/// Callers build a messages list; the provider handles the API details.
/// </summary>
public interface ILlmProvider
{
    string ModelName { get; }
    Task<string> ChatAsync(IEnumerable<ChatMessage> messages, CancellationToken ct = default);
}

/// <summary>
/// A single message in a chat conversation.
/// Role is typically "system", "user", or "assistant".
/// </summary>
public record ChatMessage(string Role, string Content);
