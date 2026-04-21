namespace ArticleWriterAgents.Agents;

/// <summary>
/// Receives tool-call events from <see cref="BaseAgent.CallWithToolsAsync"/> so they can be
/// surfaced in whatever output channel is appropriate (console, log, test spy, etc.).
/// </summary>
public interface IToolCallReporter
{
    /// <summary>Called at the start of each tool-call loop iteration.</summary>
    void ReportIteration(string agentName, int iteration, int callCount);

    /// <summary>Called just before a tool is invoked.</summary>
    void ReportToolCall(string agentName, string toolName, string argsJson);

    /// <summary>Called after a tool returns (or fails).</summary>
    void ReportToolResult(string agentName, string toolName, string result, bool isError = false);

    /// <summary>Called when the loop is terminated because the iteration limit was reached.</summary>
    void ReportMaxIterationsReached(string agentName, int max);
}
