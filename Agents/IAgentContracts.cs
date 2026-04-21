using ArticleWriterAgents.Models;

namespace ArticleWriterAgents.Agents;

/// <summary>
/// Typed contract for the research agent. Each method has a clear, single responsibility.
/// </summary>
public interface IResearchAgent
{
    /// <summary>Conducts comprehensive topic research and returns a JSON research summary.</summary>
    Task<string> ResearchAsync(ArticleRequest request, CancellationToken cancellationToken = default);

    /// <summary>Suggests key areas to cover for an article on the given topic and audience.</summary>
    Task<string[]> SuggestKeyPointsAsync(string topic, string audience, CancellationToken cancellationToken = default);
}

/// <summary>
/// Typed contract for the content creation agent, with separate methods for initial creation and revision.
/// </summary>
public interface IContentCreationAgent
{
    /// <summary>Creates an initial article draft from a research data payload.</summary>
    Task<string> CreateAsync(ArticleRequest request, string researchData, CancellationToken cancellationToken = default);

    /// <summary>
    /// Produces a revised draft that explicitly addresses the quality feedback.
    /// This uses a different prompt strategy from <see cref="CreateAsync"/>.
    /// </summary>
    Task<string> ReviseAsync(
        ArticleRequest request,
        string currentContent,
        string qualityFeedback,
        string[] revisionSuggestions,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Typed contract for the quality assurance agent.
/// The agent is the sole authority on whether a revision is required.
/// </summary>
public interface IQualityAssuranceAgent
{
    /// <summary>
    /// Assesses the quality of <paramref name="content"/> and returns a structured assessment.
    /// The returned <see cref="QualityAssessment.RequiresRevision"/> flag is the single source
    /// of truth — the orchestrator must not override it with its own threshold logic.
    /// </summary>
    Task<QualityAssessment> AssessAsync(
        ArticleRequest request,
        string content,
        int currentAttempt,
        int maxAttempts,
        double qualityThreshold,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Typed contract for the presentation agent.
/// </summary>
public interface IPresentationAgent
{
    /// <summary>Transforms finalized article content into a fully-styled HTML document.</summary>
    Task<string> FormatAsync(
        ArticleRequest request,
        string content,
        QualityAssessment? qualityAssessment,
        CancellationToken cancellationToken = default);
}
