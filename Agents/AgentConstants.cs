namespace ArticleWriterAgents.Agents;

/// <summary>
/// Shared string constants used by agents and the orchestrator.
/// Centralised here to avoid duplication and magic strings across files.
/// </summary>
public static class AgentConstants
{
    public const string ColorYellow = "yellow";
    public const string ColorGreen = "green";
    public const string ColorRed = "red";
    public const string ColorBlue = "blue";

    public const string AgentOrchestrator = "Orchestrator";
    public const string AgentResearch = "ResearchAgent";
    public const string AgentContentCreation = "ContentCreationAgent";
    public const string AgentQualityAssurance = "QualityAssuranceAgent";
    public const string AgentPresentation = "PresentationAgent";

    public const string ConversationTypeRequest = "Request";
    public const string ConversationTypeResponse = "Response";
    public const string ConversationTypeFeedback = "Feedback";
}
