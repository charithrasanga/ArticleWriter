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
        var systemPrompt = @"
You are a professional research agent with access to real-time web search. Your job is to compile
factual, well-sourced research for article writing.

TOOL USAGE STRATEGY - follow this exact sequence:
1. Call search_web_async with a broad query for the topic to get an overview and initial sources.
2. Call search_web_async with more specific queries for each key point (at least one search per key area).
3. For any specific article URL you want to include, call validate_url_async first.
   If it returns INVALID, use only the domain root (e.g. https://www.bbc.com) instead.
4. After gathering enough material, compile your findings into the JSON response below.

SOURCE RULES:
- Only include URLs returned by the search tool or verified by validate_url_async.
- Never fabricate or guess URL path segments.
- If a precise URL is unverified, use the domain root with 'note': 'domain reference only'.

RETURN a JSON object with this exact structure:
{
  ""query"": ""<search query used>"",
  ""sources"": [
    { ""name"": ""Publication / Organisation"", ""url"": ""https://verified-domain.com/optional-path"" }
  ],
  ""keyFacts"": [""<fact>""],
  ""relevantQuotes"": [""<quote with attribution>""],
  ""researchedAt"": ""<current datetime>""
}

Aim for 5-7 credible sources, 8-10 key facts, and 3-5 attributed quotes.
Return ONLY the JSON object - no preamble, no code fences.";

        var userMessage = $@"
Research the following topic thoroughly using the search tools available to you:

Topic:           {articleRequest.Topic}
Target Audience: {articleRequest.TargetAudience}
Key Points:      {string.Join(", ", articleRequest.KeyPoints)}
Tone:            {articleRequest.ToneOfVoice}

Steps:
1. Search broadly first: search_web_async(""{articleRequest.Topic} overview"")
2. Search for each key point to gather specific facts and sources
3. Validate any specific article URLs before including them

After all searches, compile and return ONLY the JSON research object.";

        var tools = new[]
        {
            _webSearch.AsAIFunction(),
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