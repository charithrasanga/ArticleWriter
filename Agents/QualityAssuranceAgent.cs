using ArticleWriterAgents.Models;
using ArticleWriterAgents.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ArticleWriterAgents.Agents;

/// <summary>
/// Assesses article quality and is the sole authority on whether a revision is required.
/// Uses a very low temperature (0.2) for deterministic, consistent scoring.
/// </summary>
public class QualityAssuranceAgent : BaseAgent, IQualityAssuranceAgent
{
    private readonly ArticleCheckTool _articleCheck;

    // QA must evaluate every section — needs adequate room
    protected override int MaxOutputTokens => 8_000;
    // Very low temperature for consistent, reproducible scoring
    protected override float Temperature => 0.2f;

    public QualityAssuranceAgent(
        IChatClient chatClient,
        ILogger<QualityAssuranceAgent> logger,
        ArticleCheckTool articleCheck,
        IToolCallReporter toolCallReporter)
        : base(chatClient, logger, toolCallReporter)
    {
        _articleCheck = articleCheck;
    }

    /// <inheritdoc/>
    public async Task<QualityAssessment> AssessAsync(
        ArticleRequest request,
        string content,
        int currentAttempt,
        int maxAttempts,
        double qualityThreshold,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Starting quality assessment (attempt {Attempt}/{Max}, threshold {Threshold}%)",
            currentAttempt, maxAttempts, qualityThreshold);

        // Convert percentage threshold to 0-10 scale (e.g. 85% → 8.5)
        var scoreThreshold = qualityThreshold / 10.0;
        var isFinalAttempt = currentAttempt >= maxAttempts;

        var systemPrompt = $@"
You are a professional quality assurance agent specialising in content review and improvement.
Your role is to evaluate articles for quality, accuracy, readability, and engagement.

CRITICAL: Return ONLY a valid JSON object — no markdown, no code blocks, no explanations.

REVISION CONTEXT:
- Current attempt: {currentAttempt} of {maxAttempts}
- Quality threshold: {qualityThreshold}% (= {scoreThreshold:F1}/10)
- This IS{(isFinalAttempt ? "" : " NOT")} the final attempt

TOOL USAGE:
- Before scoring Completeness, call count_words_async with the full article JSON string to get the actual word count.
  Compare it to the target word count and factor the result into your Completeness score.

Assessment Criteria (score 1-10 for each):
1. Clarity       — How clear and understandable the content is
2. Coherence     — Logical flow and organisation
3. Completeness  — Coverage of the topic and meeting requirements
4. Accuracy      — Factual correctness and credibility
5. Engagement    — How well it captures reader interest
6. Grammar       — Language quality and correctness
7. Structure     — Article organisation and format
8. Relevance     — Appropriateness for target audience

REVISION DECISION LOGIC (you are the sole authority):
- Set ""requiresRevision"" to TRUE  if: overall score < {scoreThreshold:F1} AND this is NOT the final attempt
- Set ""requiresRevision"" to FALSE if: overall score >= {scoreThreshold:F1} OR this IS the final attempt

Provide specific, actionable revision suggestions when requiresRevision is true.

SECTION TARGETING — when requiresRevision is true:
- Review each section of the article individually.
- In ""weakSectionIndices"", list the 0-based indices of sections that fall below the quality bar
  (e.g. [0, 2] means the 1st and 3rd sections need work).
- List only the sections that genuinely need revision. If the whole article is weak, list all indices.
- If requiresRevision is false, set weakSectionIndices to [].

RETURN ONLY THIS JSON (no markdown):
{{
  ""overallScore"": <average of all 8 scores>,
  ""clarityScore"": <1-10>,
  ""coherenceScore"": <1-10>,
  ""completenessScore"": <1-10>,
  ""accuracyScore"": <1-10>,
  ""engagementScore"": <1-10>,
  ""grammarScore"": <1-10>,
  ""structureScore"": <1-10>,
  ""relevanceScore"": <1-10>,
  ""feedback"": ""comprehensive feedback summary"",
  ""requiresRevision"": <true|false>,
  ""revisionSuggestions"": [""specific actionable suggestions""],
  ""weakSectionIndices"": [<0-based section indices that need revision, or [] if none>]
}}";

        var userMessage = $@"
Assess the quality of the following article against the original request.

ORIGINAL REQUEST:
Topic: {request.Topic}
Target Audience: {request.TargetAudience}
Word Count Target: {request.Length.Label} (≈{request.RequiredLength} words)
Tone: {request.ToneOfVoice}
Key Points: {string.Join(", ", request.KeyPoints)}

ARTICLE CONTENT:
{content}

Evaluate each criterion, compute the overall score, and apply the revision decision logic from the system prompt.
Attempt {currentAttempt}/{maxAttempts}. Threshold: {scoreThreshold:F1}/10.
Return ONLY the JSON object.";

        var tools = new[] { _articleCheck.CountWordsFunction() };
        var response = await CallWithToolsAsync(systemPrompt, userMessage, tools, cancellationToken);
        var json = ExtractJsonObject(response.Trim());

        var assessment = JsonSerializer.Deserialize<QualityAssessment>(json)
            ?? throw new InvalidOperationException("Quality assessment returned null after deserialisation");

        _logger.LogInformation(
            "Quality assessment complete: score={Score:F1}/10, requiresRevision={Requires}",
            assessment.OverallScore, assessment.RequiresRevision);

        return assessment;
    }

    private static string ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }
}

