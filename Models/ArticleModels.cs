using System.Text.Json.Serialization;

namespace ArticleWriterAgents.Models;

/// <summary>
/// Named length tiers — each carries a human label and an approximate word-count target
/// used in agent prompts. Keeping this as a value object avoids magic integers throughout
/// the codebase.
/// </summary>
public record ArticleLength(string Label, int ApproximateWords)
{
    public override string ToString() => Label;

    // ── Well-known tiers ────────────────────────────────────────────────────
    public static readonly ArticleLength Brief         = new("Brief (≈600 words)",          600);
    public static readonly ArticleLength Standard      = new("Standard (≈1,200 words)",    1_200);
    public static readonly ArticleLength Detailed      = new("Detailed (≈2,000 words)",    2_000);
    public static readonly ArticleLength Comprehensive = new("Comprehensive (≈3,500 words)", 3_500);

    public static readonly ArticleLength[] All = [Brief, Standard, Detailed, Comprehensive];

    /// <summary>Looks up a tier by its label; falls back to Standard.</summary>
    public static ArticleLength FromLabel(string label)
        => All.FirstOrDefault(t => t.Label == label) ?? Standard;
}

// Updated ArticleRequest to match Program.cs usage
public record ArticleRequest(
    string Topic,
    string TargetAudience,
    ArticleLength Length,
    string ToneOfVoice,
    string[] KeyPoints
)
{
    /// <summary>Convenience accessor so agents can read the target word count directly.</summary>
    public int RequiredLength => Length.ApproximateWords;
}

public record ArticleSection(
    string Title,
    string Content,
    int WordCount
);

public record ArticleDraft(
    string Title,
    string Introduction,
    List<ArticleSection> Sections,
    string Conclusion,
    List<string> Sources,
    int TotalWordCount,
    DateTime CreatedAt
);

public record QualityScore(
    string Aspect,
    int Score, // 1-10
    string Feedback,
    List<string> Suggestions
);

// Updated QualityAssessment to match agent usage with decimal scores
public record QualityAssessment(
    [property: JsonPropertyName("overallScore")] double OverallScore,
    [property: JsonPropertyName("clarityScore")] double ClarityScore,
    [property: JsonPropertyName("coherenceScore")] double CoherenceScore,
    [property: JsonPropertyName("completenessScore")] double CompletenessScore,
    [property: JsonPropertyName("accuracyScore")] double AccuracyScore,
    [property: JsonPropertyName("engagementScore")] double EngagementScore,
    [property: JsonPropertyName("grammarScore")] double GrammarScore,
    [property: JsonPropertyName("structureScore")] double StructureScore,
    [property: JsonPropertyName("relevanceScore")] double RelevanceScore,
    [property: JsonPropertyName("feedback")] string Feedback,
    [property: JsonPropertyName("requiresRevision")] bool RequiresRevision,
    [property: JsonPropertyName("revisionSuggestions")] string[] RevisionSuggestions
);

public record ResearchResult(
    string Query,
    List<string> Sources,
    List<string> KeyFacts,
    List<string> RelevantQuotes,
    DateTime ResearchedAt
);

public record PresentationFormat(
    string Format, // "HTML", "Markdown", "PDF"
    string Content,
    Dictionary<string, object> Metadata
);

public enum AgentType
{
    Research,
    ContentCreation,
    QualityAssurance,
    Presentation
}

public enum WorkflowStatus
{
    NotStarted,
    Researching,
    CreatingContent,
    AssessingQuality,
    Revising,
    Formatting,
    Completed,
    Failed
}

public record WorkflowState(
    string WorkflowId,
    ArticleRequest Request,
    WorkflowStatus Status,
    ResearchResult? Research = null,
    ArticleDraft? Draft = null,
    QualityAssessment? Assessment = null,
    PresentationFormat? FinalArticle = null,
    List<string>? ErrorMessages = null,
    DateTime CreatedAt = default,
    DateTime? CompletedAt = null
);

public record AgentConversation(
    string FromAgent,
    string ToAgent,
    string Message,
    DateTime Timestamp,
    string ConversationType // "Request", "Response", "Feedback"
);

/// <summary>
/// Configuration for the article generation workflow.
/// Populated from the "ArticleGeneration" section of appsettings.
/// </summary>
public class ArticleGenerationConfig
{
    /// <summary>
    /// Overall quality score (0–100) an article must reach before revisions stop.
    /// Corresponds to 8.5/10 on the 0–10 agent scoring scale.
    /// </summary>
    public double QualityThreshold { get; set; } = 85.0;

    /// <summary>Maximum QA + revision cycles before the result is accepted as-is.</summary>
    public int MaxRevisions { get; set; } = 3;

    /// <summary>Audience choices shown in the interactive prompt.</summary>
    public string[] AudienceChoices { get; set; } = [];

    /// <summary>Tone choices shown in the interactive prompt.</summary>
    public string[] ToneChoices { get; set; } = [];
}

/// <summary>Image embedding configuration, bound from the "Images" appsettings section.</summary>
public class ImageConfig
{
    /// <summary>Base URL of the Unsplash API (https://api.unsplash.com).</summary>
    public string ProviderUrl { get; set; } = "https://api.unsplash.com";

    /// <summary>Unsplash application Access Key — required for all API calls.</summary>
    public string AccessKey { get; set; } = "";

    /// <summary>Pixel dimensions for the article hero image, as WxH (e.g. "1200x500").</summary>
    public string HeaderSize { get; set; } = "1200x500";

    /// <summary>Pixel dimensions for per-section images, as WxH (e.g. "800x450").</summary>
    public string SectionSize { get; set; } = "800x450";

    /// <summary>
    /// Parses a WxH size string into its numeric components.
    /// Returns (0, 0) for malformed values so callers can fall back gracefully.
    /// </summary>
    public (int Width, int Height) ParseSize(string size)
    {
        var parts = size.Split('x');
        if (parts.Length == 2
            && int.TryParse(parts[0], out var w)
            && int.TryParse(parts[1], out var h))
            return (w, h);
        return (0, 0);
    }

    /// <summary>Builds a <c>/photos/random</c> API request URL for the given search query and dimensions.</summary>
    public string BuildApiUrl(string size, string query)
    {
        var (w, h) = ParseSize(size);
        var baseUrl = ProviderUrl.TrimEnd('/');
        var encodedQuery = Uri.EscapeDataString(query);
        return w > 0
            ? $"{baseUrl}/photos/random?query={encodedQuery}&orientation=landscape&w={w}&h={h}&client_id={AccessKey}"
            : $"{baseUrl}/photos/random?query={encodedQuery}&orientation=landscape&client_id={AccessKey}";
    }
}

/// <summary>The outcome of a completed article generation workflow.</summary>
public record ArticleWorkflowResult(
    ArticleRequest OriginalRequest,
    /// <summary>Final article in its source (JSON) form, as produced by the content agent.</summary>
    string FinalContent,
    /// <summary>Fully formatted HTML output from the presentation agent.</summary>
    string FormattedContent,
    QualityAssessment? QualityAssessment,
    double FinalQualityScore,
    /// <summary>Number of revision cycles performed (0 = article passed QA on the first attempt).</summary>
    int RevisionCount,
    DateTimeOffset CompletedAt
);

/// <summary>Configuration for the Serper Google Search API, bound from "Serper" in appsettings.</summary>
public class SerperConfig
{
    /// <summary>Serper API key — required for web search calls.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Maximum number of organic results to retrieve per query (default 10).</summary>
    public int ResultCount { get; set; } = 10;
}
