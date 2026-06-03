using System.Net.Http.Json;
using System.Text.RegularExpressions;
using ChefAgent.Agents.Diet;
using ChefAgent.Shared;
using ChefAgent.Shared.Models;
using Microsoft.Extensions.Logging;

namespace ChefAgent.Agents.Recipe;

/// <summary>
/// Preprocesses queries before vector search and validates results after.
/// Handles two failure modes that pure vector search can't fix:
///   1. Negation ("pasta without tomatoes") — parsed out pre-search, filtered post-search
///   2. Abstraction ("something warm and comforting") — expanded to concrete terms via LLM
/// </summary>
public class QueryPreprocessor
{
    private readonly HttpClient _httpClient;
    private readonly string _ollamaUrl;
    private readonly string _chatModel;
    private readonly ILogger<QueryPreprocessor> _logger;

    private static readonly Regex NegationPattern = new(
        @"\b(?:without|no|excluding|exclude|free of|hold the|skip the|minus)\s+(?:the\s+)?(.+?)(?:\s*(?:,|and)\s*|\s*$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex FreePattern = new(
        @"(\w+)-free",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );
    private static readonly Lazy<SymSpell> _symSpell = new(() =>
    {
        var symSpell = new SymSpell(initialCapacity: 82765, maxDictionaryEditDistance: 2);
        var dictPath = Path.Combine(
            AppContext.BaseDirectory,
            "Dictionaries",
            "frequency_dictionary_en_80k.txt"
        );
        symSpell.LoadDictionary(dictPath, termIndex: 0, countIndex: 1);
        return symSpell;
    });
    private static readonly Lazy<Dictionary<string, string>> _foodCorrections = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Dictionaries", "food_corrections.json");
        if (!File.Exists(path))
            return new Dictionary<string, string>();
        var json = File.ReadAllText(path);
        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            ?? new Dictionary<string, string>();
    });

    /// <summary>
    /// Corrects misspelled words in the query using Hunspell dictionary.
    /// Skips correction for known food/dietary terms to avoid over-correction.
    /// Returns original word if no suggestion found.
    /// </summary>
    public string CorrectSpelling(string query)
    {
        var skipWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "gluten-free",
            "dairy-free",
            "nut-free",
            "egg-free",
            "soy-free",
            "vegan",
            "vegetarian",
            "pescatarian",
            "halal",
            "kosher",
            "sattvic",
            "jain",
            "tikka",
            "masala",
            "carbonara",
            "stroganoff",
            "bolognese",
            "parmesan",
            "guacamole",
            "tiramisu",
            "ramen",
            "pho",
            "bibimbap",
            "shakshuka",
            "sriracha",
            "tahini",
            "hummus",
            "tzatziki",
            "kimchi",
        };

        var foodCorrections = _foodCorrections.Value;
        var symSpell = _symSpell.Value;
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var corrected = new List<string>();

        foreach (var word in words)
        {
            // Step 1: food domain lookup — highest confidence, no ambiguity
            if (foodCorrections.TryGetValue(word.ToLowerInvariant(), out var foodFixed))
            {
                corrected.Add(foodFixed);
                _logger.LogInformation(
                    "[QueryPreprocessor] Food dict corrected: \"{Original}\" → \"{Corrected}\"",
                    word,
                    foodFixed
                );
                continue;
            }

            // Step 2: skip known food/dietary terms and short words
            if (skipWords.Contains(word) || word.Contains('-') || word.Length <= 2)
            {
                corrected.Add(word);
                continue;
            }

            // Step 3: SymSpell — frequency-weighted, picks most common valid word
            var suggestions = symSpell.Lookup(
                word.ToLowerInvariant(),
                SymSpell.Verbosity.Top, // top suggestion only
                maxEditDistance: 2
            );

            if (suggestions.Count > 0 && suggestions[0].term != word.ToLowerInvariant())
            {
                var best = suggestions[0].term;
                corrected.Add(best);
                _logger.LogInformation(
                    "[QueryPreprocessor] SymSpell corrected: \"{Original}\" → \"{Corrected}\" (freq={Freq})",
                    word,
                    best,
                    suggestions[0].count
                );
            }
            else
            {
                corrected.Add(word);
            }
        }

        return string.Join(" ", corrected);
    }

    // Signals that a query is abstract/vague and needs expansion
    private static readonly string[] AbstractSignals =
    [
        "something",
        "anything",
        "whatever",
        "surprise me",
        "feeling like",
        "in the mood",
        "craving",
        "warm",
        "comforting",
        "cozy",
        "light",
        "healthy",
        "quick",
        "easy",
        "fancy",
        "impressive",
        "special",
    ];

    public QueryPreprocessor(
        HttpClient httpClient,
        string ollamaUrl,
        string chatModel,
        ILogger<QueryPreprocessor> logger
    )
    {
        _httpClient = httpClient;
        _ollamaUrl = ollamaUrl;
        _chatModel = chatModel;
        _logger = logger;
    }

    // ──────────────────────────────────────────────
    //  PRE-SEARCH: Parse negation + expand abstractions
    // ──────────────────────────────────────────────

    /// <summary>
    /// Full pre-processing pipeline: detect negation, expand if abstract.
    /// Returns a cleaned/expanded query ready for embedding, plus excluded terms for post-filtering.
    /// </summary>
    public async Task<PreprocessedQuery> PreprocessAsync(
        string query,
        bool expand = true,
        CancellationToken ct = default
    )
    {
        // Step 0: Correct spelling before any other processing
        var spellingCorrected = CorrectSpelling(query);
        if (spellingCorrected != query)
            _logger.LogInformation(
                "[QueryPreprocessor] Spelling corrected: \"{Original}\" → \"{Corrected}\"",
                query,
                spellingCorrected
            );

        // Step 1: Parse negation
        var negationResult = ParseNegation(spellingCorrected);
        var cleanedQuery = negationResult.CleanedQuery;
        var excludedTerms = negationResult.ExcludedTerms;

        // Step 2: Expand if abstract
        var expandedQuery = cleanedQuery;
        if (expand && IsAbstract(cleanedQuery))
        {
            try
            {
                expandedQuery = await ExpandQueryAsync(cleanedQuery, ct);
                _logger.LogInformation(
                    "Query expanded: \"{Original}\" → \"{Expanded}\"",
                    cleanedQuery,
                    expandedQuery
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Query expansion failed, using original query");
                expandedQuery = cleanedQuery;
            }
        }

        return new PreprocessedQuery(
            OriginalQuery: query,
            SearchQuery: expandedQuery,
            ExcludedTerms: excludedTerms,
            WasExpanded: expandedQuery != spellingCorrected
        );
    }

    /// <summary>
    /// Parses negation phrases out of the query.
    /// "pasta without tomatoes and onions" → cleaned: "pasta", excluded: ["tomatoes", "onions"]
    /// </summary>
    public NegationResult ParseNegation(string query)
    {
        var excludedTerms = new List<string>();
        var cleanedQuery = query;

        // Extract explicit negation phrases like "without tomatoes" or "exclude onions".
        var matches = NegationPattern.Matches(query);
        foreach (Match match in matches)
        {
            var term = match.Groups[1].Value.Trim().ToLowerInvariant();

            // Split multi-exclusions: "without tomatoes and onions"
            var terms = Regex.Split(term, @"\s+(?:and|or)\s+", RegexOptions.IgnoreCase);
            foreach (var t in terms)
            {
                var cleaned = t.Trim();
                if (!string.IsNullOrEmpty(cleaned))
                    excludedTerms.Add(cleaned);
            }

            cleanedQuery = cleanedQuery.Replace(match.Value, " ").Trim();
        }

        // Handle "X-free" patterns, e.g. "gluten-free" or "dairy-free".
        // Expand category name to full ingredient set using DietaryCategoryMap.
        // "dairy-free" → exclude milk, cream, butter, cheese, etc. (not just "dairy")
        var freeMatches = FreePattern.Matches(cleanedQuery);
        foreach (Match match in freeMatches)
        {
            var category = match.Groups[1].Value.ToLowerInvariant();
            var ingredients = DietaryRules.GetCategoryIngredients(category);

            if (ingredients is not null)
            {
                excludedTerms.AddRange(ingredients);
                _logger.LogInformation(
                    "[QueryPreprocessor] Expanded '{Category}-free' → {Count} exclusion terms",
                    category,
                    ingredients.Count
                );
            }
            else
            {
                // Unknown category — fall back to raw term (old behavior)
                excludedTerms.Add(category);
                _logger.LogInformation(
                    "[QueryPreprocessor] Unknown category '{Category}-free' — using raw term as exclusion",
                    category
                );
            }
        }

        if (excludedTerms.Count > 0)
        {
            _logger.LogInformation(
                "Negation detected — excluded: [{Excluded}], cleaned query: \"{Cleaned}\"",
                string.Join(", ", excludedTerms),
                cleanedQuery
            );
        }

        return new NegationResult(cleanedQuery, excludedTerms);
    }

    /// <summary>
    /// Detects whether a query is abstract/vague and would benefit from expansion.
    /// </summary>
    public bool IsAbstract(string query)
    {
        var lower = query.ToLowerInvariant();
        return AbstractSignals.Any(signal => lower.Contains(signal));
    }

    /// <summary>
    /// Expands a vague query into concrete food search terms via LLM.
    /// "something warm and comforting" → "soup, stew, chili, casserole, pot roast"
    /// </summary>
    private async Task<string> ExpandQueryAsync(string query, CancellationToken ct)
    {
        var prompt = $"""
            You are a recipe search assistant. The user's query is vague or abstract. 
            Rewrite it as a concrete recipe search query using specific food terms.

            Examples:
            - "something warm and comforting" → "soup, stew, chili, casserole, pot roast"
            - "quick and easy weeknight meal" → "30 minute chicken, one pot pasta, sheet pan dinner, stir fry"
            - "something impressive for guests" → "beef wellington, lobster, souffle, rack of lamb"
            - "healthy and light" → "grilled fish, salad, steamed vegetables, grain bowl"

            User query: "{query}"

            Return ONLY the expanded search terms, nothing else. No explanation.
            """;

        var request = new
        {
            model = _chatModel,
            messages = new[] { new { role = "user", content = prompt } },
            stream = false,
        };

        var response = await _httpClient.PostAsJsonAsync($"{_ollamaUrl}/api/chat", request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(ct);
        var expanded = result?.Message?.Content?.Trim() ?? query;

        // Strip quotes if the LLM wraps it
        expanded = expanded.Trim('"', '\'');

        return string.IsNullOrWhiteSpace(expanded) ? query : expanded;
    }

    // ──────────────────────────────────────────────
    //  POST-SEARCH: Filter negation violations
    // ──────────────────────────────────────────────

    /// <summary>
    /// Removes recipes whose ingredients contain any excluded term.
    /// </summary>
    public List<RecipeDocument> FilterNegations(
        List<RecipeDocument> recipes,
        List<string> excludedTerms
    )
    {
        if (excludedTerms.Count == 0)
            return recipes;

        // Remove recipes whose ingredient list contains any of the excluded terms.
        var before = recipes.Count;

        var filtered = recipes
            .Where(recipe =>
            {
                foreach (var excluded in excludedTerms)
                {
                    foreach (var ingredient in recipe.Ingredients)
                    {
                        if (ingredient.Contains(excluded, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogInformation(
                                "Filtered out \"{Title}\" — \"{Ingredient}\" matches exclusion \"{Excluded}\"",
                                recipe.Title,
                                ingredient,
                                excluded
                            );
                            return false;
                        }
                    }
                }
                return true;
            })
            .ToList();

        _logger.LogInformation(
            "Negation filter: {Before} → {After} recipes ({Removed} removed)",
            before,
            filtered.Count,
            before - filtered.Count
        );

        return filtered;
    }

    // DTOs
    private record OllamaChatResponse(OllamaChatMessage Message);

    private record OllamaChatMessage(string Role, string Content);
}

public record PreprocessedQuery(
    string OriginalQuery,
    string SearchQuery,
    List<string> ExcludedTerms,
    bool WasExpanded
);

public record NegationResult(string CleanedQuery, List<string> ExcludedTerms);
