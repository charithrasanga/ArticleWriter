using ArticleWriterAgents.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArticleWriterAgents.Agents;

/// <summary>
/// Orchestrates the four-agent article generation workflow:
/// Research → Content Creation → QA loop (with targeted revisions) → Presentation.
///
/// Design decisions:
/// - Depends on typed agent interfaces, not concrete classes → fully testable with mocks.
/// - The QA agent is the sole authority on RequiresRevision — the orchestrator never
///   overrides that decision with its own threshold maths.
/// - <see cref="ArticleWorkflowResult.RevisionCount"/> counts actual revision cycles
///   (0 = passed QA on the first attempt, 1 = one revision was needed, etc.).
/// </summary>
public class ArticleWorkflowOrchestrator
{
    private readonly IResearchAgent _researchAgent;
    private readonly IContentCreationAgent _contentCreationAgent;
    private readonly IQualityAssuranceAgent _qaAgent;
    private readonly IPresentationAgent _presentationAgent;
    private readonly ILogger<ArticleWorkflowOrchestrator> _logger;
    private readonly ArticleGenerationConfig _config;

    public ArticleWorkflowOrchestrator(
        IResearchAgent researchAgent,
        IContentCreationAgent contentCreationAgent,
        IQualityAssuranceAgent qaAgent,
        IPresentationAgent presentationAgent,
        ILogger<ArticleWorkflowOrchestrator> logger,
        IOptions<ArticleGenerationConfig> config)
    {
        _researchAgent = researchAgent;
        _contentCreationAgent = contentCreationAgent;
        _qaAgent = qaAgent;
        _presentationAgent = presentationAgent;
        _logger = logger;
        _config = config.Value;
    }

    public async Task<ArticleWorkflowResult> ProcessArticleRequestAsync(
        ArticleRequest request,
        IProgress<(string phase, double progress)>? progress = null,
        List<AgentConversation>? conversations = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting article workflow for topic: {Topic}", request.Topic);

        try
        {
            // ── Step 1: Research ────────────────────────────────────────────────
            progress?.Report(("Research phase", 10));
            Log(conversations, AgentConstants.AgentOrchestrator, AgentConstants.AgentResearch,
                $"Research request: {request.Topic}", AgentConstants.ConversationTypeRequest);

            var researchData = await _researchAgent.ResearchAsync(request, cancellationToken);

            Log(conversations, AgentConstants.AgentResearch, AgentConstants.AgentOrchestrator,
                "Research completed", AgentConstants.ConversationTypeResponse);

            // ── Step 2: Initial content creation ───────────────────────────────
            progress?.Report(("Content creation", 30));
            Log(conversations, AgentConstants.AgentOrchestrator, AgentConstants.AgentContentCreation,
                "Initial content creation request", AgentConstants.ConversationTypeRequest);

            var currentContent = await _contentCreationAgent.CreateAsync(request, researchData, cancellationToken);

            Log(conversations, AgentConstants.AgentContentCreation, AgentConstants.AgentOrchestrator,
                "Initial draft completed", AgentConstants.ConversationTypeResponse);

            // ── Step 3: QA + targeted revision loop ────────────────────────────
            // The QA agent's RequiresRevision flag is the sole authority.
            // revisionCount = number of times we actually revised the content (not loop iterations).
            QualityAssessment? finalAssessment = null;
            int revisionCount = 0;
            var maxRevisions = _config.MaxRevisions;

            for (int attempt = 1; attempt <= maxRevisions; attempt++)
            {
                progress?.Report(($"Quality check #{attempt}", 60 + attempt * 7));
                Log(conversations, AgentConstants.AgentOrchestrator, AgentConstants.AgentQualityAssurance,
                    $"Quality assessment #{attempt}", AgentConstants.ConversationTypeRequest);

                finalAssessment = await _qaAgent.AssessAsync(
                    request, currentContent, attempt, maxRevisions, _config.QualityThreshold, cancellationToken);

                Log(conversations, AgentConstants.AgentQualityAssurance, AgentConstants.AgentOrchestrator,
                    $"Assessment #{attempt}: score={finalAssessment.OverallScore:F1}/10, requiresRevision={finalAssessment.RequiresRevision}",
                    AgentConstants.ConversationTypeFeedback);

                _logger.LogInformation(
                    "QA attempt {Attempt}/{Max}: score={Score:F1}/10, requiresRevision={Requires}",
                    attempt, maxRevisions, finalAssessment.OverallScore, finalAssessment.RequiresRevision);

                // QA agent is the authority — if it says no revision needed, we stop.
                if (!finalAssessment.RequiresRevision)
                    break;

                // Revise only if there are remaining attempts.
                if (attempt < maxRevisions)
                {
                    progress?.Report(($"Revising content (revision {revisionCount + 1})", 65 + attempt * 7));
                    Log(conversations, AgentConstants.AgentQualityAssurance, AgentConstants.AgentContentCreation,
                        $"Revision needed: {finalAssessment.Feedback}", AgentConstants.ConversationTypeFeedback);

                    currentContent = await _contentCreationAgent.ReviseAsync(
                        request,
                        currentContent,
                        finalAssessment.Feedback,
                        finalAssessment.RevisionSuggestions,
                        cancellationToken);

                    revisionCount++;

                    Log(conversations, AgentConstants.AgentContentCreation, AgentConstants.AgentQualityAssurance,
                        $"Revision {revisionCount} completed", AgentConstants.ConversationTypeResponse);
                }
            }

            // ── Step 4: Presentation formatting ────────────────────────────────
            progress?.Report(("Presentation formatting", 90));
            Log(conversations, AgentConstants.AgentOrchestrator, AgentConstants.AgentPresentation,
                "Presentation formatting request", AgentConstants.ConversationTypeRequest);

            var formattedContent = await _presentationAgent.FormatAsync(
                request, currentContent, finalAssessment, cancellationToken);

            Log(conversations, AgentConstants.AgentPresentation, AgentConstants.AgentOrchestrator,
                "Formatting complete", AgentConstants.ConversationTypeResponse);

            progress?.Report(("Finalising", 98));
            _logger.LogInformation(
                "Article workflow completed. Revisions performed: {RevisionCount}", revisionCount);

            return new ArticleWorkflowResult(
                OriginalRequest: request,
                FinalContent: currentContent,
                FormattedContent: formattedContent,
                QualityAssessment: finalAssessment,
                FinalQualityScore: finalAssessment?.OverallScore ?? 0,
                RevisionCount: revisionCount,
                CompletedAt: DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Article workflow failed for topic: {Topic}", request.Topic);
            throw;
        }
    }

    private static void Log(
        List<AgentConversation>? conversations,
        string from, string to, string message, string type)
    {
        conversations?.Add(new AgentConversation(from, to, message, DateTime.Now, type));
    }
}
