namespace ChefAgent.Shared;

public record InputValidationResult
{
    public bool IsValid { get; init; }
    public string? RejectionReason { get; init; }
    public string SanitizedMessage { get; init; } = string.Empty;
}

public static class InputGuard
{
    private const int MaxMessageLength = 500;
    private const int MinMessageLength = 2;

    // ── Stage 1: Prompt injection detection ──────────────────────────
    // Two-signal approach: trigger verb + system-directed target
    // "ignore the garlic" → trigger verb + food noun → safe
    // "ignore your instructions" → trigger verb + system target → blocked

    private static readonly HashSet<string> InjectionTriggerVerbs =
    [
        "ignore",
        "disregard",
        "forget",
        "override",
        "bypass",
        "skip",
        "abandon",
        "drop",
        "dismiss",
        "suppress",
    ];

    private static readonly HashSet<string> SystemTargets =
    [
        "instructions",
        "instruction",
        "rules",
        "guidelines",
        "constraints",
        "your role",
        "your purpose",
        "previous prompt",
        "system prompt",
        "above prompt",
        "prior instructions",
        "original instructions",
        "safety",
        "guardrails",
        "filters",
        "restrictions",
        "all of the above",
        "everything above",
        "everything before",
    ];

    // Full-phrase injection patterns — no two-signal check needed,
    // these are unambiguous
    private static readonly HashSet<string> DirectInjectionPhrases =
    [
        "you are now",
        "act as",
        "pretend to be",
        "roleplay as",
        "you are a",
        "behave as",
        "switch to",
        "enter mode",
        "system prompt:",
        "### instruction",
        "### system",
        "[system]",
        "[inst]",
        "<<sys>>",
        "do not act as a",
        "stop being a",
        "respond as if you are",
        "from now on you are",
        "new persona",
        "new role",
        "new instructions",
        "jailbreak",
        "dan mode",
        "developer mode",
    ];

    // ── Public API ───────────────────────────────────────────────────

    public static InputValidationResult Validate(string? message)
    {
        // Stage 1: null / empty / whitespace
        if (string.IsNullOrWhiteSpace(message))
            return Reject(
                "Please enter a message — I can help you find recipes, check dietary compatibility, or plan meals."
            );

        // Stage 2: sanitize control characters + excessive whitespace
        var sanitized = Sanitize(message);

        // Stage 3: length checks (after sanitization — whitespace collapse may shorten)
        if (sanitized.Length < MinMessageLength)
            return Reject(
                "That message is too short. Try something like \"find me a quick chicken dinner.\""
            );

        if (sanitized.Length > MaxMessageLength)
            return Reject(
                $"Messages are limited to {MaxMessageLength} characters. Try shortening your query."
            );

        // Stage 4: prompt injection detection
        var injectionReason = DetectInjection(sanitized);
        if (injectionReason is not null)
            return Reject(
                "I can help you find recipes, plan meals, and check dietary compatibility. What would you like to do?"
            );
        // Neutral response — don't reveal that injection was detected

        return new InputValidationResult { IsValid = true, SanitizedMessage = sanitized };
    }

    // ── Internals ────────────────────────────────────────────────────

    private static string Sanitize(string input)
    {
        // Strip control characters (keep newlines and tabs — users paste recipes)
        var chars = input.Where(c => !char.IsControl(c) || c == '\n' || c == '\t').ToArray();
        var cleaned = new string(chars);

        // Collapse excessive whitespace
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ").Trim();

        // Strip null bytes explicitly (defense in depth)
        cleaned = cleaned.Replace("\0", "");

        return cleaned;
    }

    private static string? DetectInjection(string message)
    {
        var lower = message.ToLowerInvariant();

        // Check 1: direct injection phrases — unambiguous, no context needed
        foreach (var phrase in DirectInjectionPhrases)
        {
            if (lower.Contains(phrase))
                return phrase;
        }

        // Check 2: two-signal detection — trigger verb + system target
        // Only flag if both are present in the same message
        var hasTriggerVerb = false;
        foreach (var verb in InjectionTriggerVerbs)
        {
            // Word boundary check: "ignore" should match "ignore your..."
            // but not "reignore" (not a real word, but defense in depth)
            var idx = lower.IndexOf(verb, StringComparison.Ordinal);
            if (idx >= 0)
            {
                var beforeOk = idx == 0 || !char.IsLetter(lower[idx - 1]);
                var afterOk =
                    idx + verb.Length >= lower.Length || !char.IsLetter(lower[idx + verb.Length]);
                if (beforeOk && afterOk)
                {
                    hasTriggerVerb = true;
                    break;
                }
            }
        }

        if (hasTriggerVerb)
        {
            foreach (var target in SystemTargets)
            {
                if (lower.Contains(target))
                    return $"{target} (two-signal)";
            }
        }

        return null; // No injection detected
    }

    private static InputValidationResult Reject(string reason) =>
        new()
        {
            IsValid = false,
            RejectionReason = reason,
            SanitizedMessage = string.Empty,
        };
}
