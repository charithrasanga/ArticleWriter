using ArticleWriterAgents.Models;
using ArticleWriterAgents.Tools;
using ArticleWriterAgents.Utils;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ArticleWriterAgents.Agents;

public class ResearchAgent : BaseAgent, IResearchAgent
{
    private readonly UrlValidator _urlValidator;
    private readonly WebSearchTool _webSearch;
    private readonly UrlValidatorTool _urlValidatorTool;

    // Research output can be verbose — give it enough room
    protected override int MaxOutputTokens => 8_000;
    // Lower temperature for factual, consistent research outputs
    protected override float Temperature => 0.3f;

    public ResearchAgent(
        IChatClient chatClient,
        ILogger<ResearchAgent> logger,
        UrlValidator urlValidator,
        WebSearchTool webSearch,
        UrlValidatorTool urlValidatorTool,
        IToolCallReporter toolCallReporter)
        : base(chatClient, logger, toolCallReporter)
    {
        _urlValidator     = urlValidator;
        _webSearch        = webSearch;
        _urlValidatorTool = urlValidatorTool;
    }

    /// <inheritdoc/>
    public async Task<string> ResearchAsync(ArticleRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting research for topic: {Topic}", request.Topic);
        return await ConductTopicResearchAsync(request, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string[]> SuggestKeyPointsAsync(
        string topic,
        string audience,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating key-point suggestions for topic: {Topic}", topic);

        var systemPrompt = @"
You are an expert content strategist who decides which key areas an article must cover.
Your role is to analyse a topic and propose the most important angles for a specific audience.

Guidelines:
- Determine how many key points the topic genuinely requires — do NOT default to a fixed number.
  Simple or narrow topics may need only 3-4 points; broad, complex, or multi-disciplinary
  topics may need 8-10 or more. Match depth to complexity.
- Consider the target audience's background, needs, and expected depth of knowledge.
- Each point must be distinct — avoid overlap or redundancy.
- Points should together give comprehensive, end-to-end coverage of the topic.
- Make each suggestion specific and actionable (not vague like ""Overview"" or ""Conclusion"").

Return ONLY a JSON array of strings, one per key area.
Example (5 items — the count is illustrative, not a rule):
[""Historical origins and timeline"", ""Core mechanisms and how it works"", ""Real-world applications"", ""Common misconceptions"", ""Future directions and emerging research""]";

        var userMessage = $@"
Analyse the following article brief and propose as many key points as the topic genuinely requires.
Do not default to any fixed number — let the topic's breadth and the audience's needs determine the count.

Topic: {topic}
Target Audience: {audience}

Return ONLY the JSON array — no additional text or formatting.";

        var response = await CallAsync(systemPrompt, userMessage, cancellationToken);
        var clean = ExtractJsonArray(response.Trim());

        try
        {
            return JsonSerializer.Deserialize<string[]>(clean) ?? Array.Empty<string>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Could not parse key-point suggestions as JSON array; returning empty");
            return Array.Empty<string>();
        }
    }

    private async Task<string> ConductTopicResearchAsync(ArticleRequest articleRequest, CancellationToken cancellationToken)
    {
        // ── Step 1: Fire all searches in parallel ────────────────────────────
        // One broad overview query + one focused query per key point.
        var queries = new List<string>
        {
            $"{articleRequest.Topic} overview {articleRequest.TargetAudience}"
        };
        queries.AddRange(articleRequest.KeyPoints.Select(kp => $"{articleRequest.Topic} {kp}"));

        _logger.LogInformation("ResearchAgent: firing {Count} search queries in parallel", queries.Count);

        var searchTasks = queries
            .Select(q => _webSearch.SearchWebAsync(q))
            .ToList();

        string[] searchResults = await Task.WhenAll(searchTasks);

        // ── Step 2: Compile all results into a single context block ──────────
        var contextBuilder = new System.Text.StringBuilder();
        for (int i = 0; i < queries.Count; i++)
        {
            contextBuilder.AppendLine($"=== Search: {queries[i]} ===");
            contextBuilder.AppendLine(searchResults[i]);
            contextBuilder.AppendLine();
        }
        var preSearchedContext = contextBuilder.ToString();

        // ── Step 3: LLM synthesises from pre-fetched data ────────────────────
        // The LLM only needs validate_url_async now — all searches are already done.
        var systemPrompt = @"
You are a professional research agent. All web searches have already been performed for you.
Your job is to synthesise the provided search results into a structured research JSON object.

URL VALIDATION:
- For any specific article or page URL you want to include, call validate_url_async first.
- If it returns INVALID, use only the domain root (e.g. https://www.bbc.com) instead.
- Well-known domains (wikipedia.org, reuters.com, bbc.com, etc.) are pre-validated — no need to call validate_url_async for them.

SOURCE RULES:
- Only include URLs that appear in the search results below or are verified by validate_url_async.
- Never fabricate or guess URL path segments.

RETURN a JSON object with this exact structure:
{
  ""query"": ""<main topic searched>"",
  ""sources"": [
    { ""name"": ""Publication / Organisation"", ""url"": ""https://verified-domain.com/optional-path"" }
  ],
  ""keyFacts"": [""<fact>""],
  ""relevantQuotes"": [""<quote with attribution>""],
  ""researchedAt"": ""<current datetime>""
}

Aim for 5-7 credible sources, 8-10 key facts, and 3-5 attributed quotes.
Return ONLY the JSON object — no preamble, no code fences.";

        var userMessage = $@"
Synthesise research for the following article brief using the search results provided below.

Topic:           {articleRequest.Topic}
Target Audience: {articleRequest.TargetAudience}
Key Points:      {string.Join(", ", articleRequest.KeyPoints)}
Tone:            {articleRequest.ToneOfVoice}

--- PRE-FETCHED SEARCH RESULTS ---
{preSearchedContext}
--- END OF SEARCH RESULTS ---

Using only the above results, compile and return the JSON research object.
Validate any specific article URLs you wish to include using validate_url_async before adding them.";

        var tools = new[]
        {
            _urlValidatorTool.AsAIFunction()
        };

        var response = await CallWithToolsAsync(systemPrompt, userMessage, tools, cancellationToken);

        _logger.LogInformation("Research completed for topic: {Topic}", articleRequest.Topic);
        return response;
    }

    private static string ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }

    private static string ExtractJsonArray(string text)
    {
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }
}