using ArticleWriterAgents.Agents;
using Spectre.Console;

namespace ArticleWriterAgents.Presentation;

/// <summary>
/// Writes tool-call events to the Spectre.Console output during article generation.
/// When called inside an <c>AnsiConsole.Live</c> context the lines scroll above the live widget;
/// outside Live they render inline. A lock ensures interleaved calls from different agents
/// never produce garbled output.
/// </summary>
public sealed class ConsoleToolCallReporter : IToolCallReporter
{
    private readonly object _lock = new();

    /// <inheritdoc/>
    public void ReportIteration(string agentName, int iteration, int callCount)
    {
        lock (_lock)
        {
            AnsiConsole.MarkupLine(
                $"  [dim][[{agentName.EscapeMarkup()}]][/] " +
                $"[grey]tool loop · iteration {iteration} · {callCount} call(s)[/]");
        }
    }

    /// <inheritdoc/>
    public void ReportToolCall(string agentName, string toolName, string argsJson)
    {
        // Trim the args to keep output readable (long queries, big JSON objects)
        var args = argsJson.Length > 160 ? argsJson[..160] + "…" : argsJson;

        lock (_lock)
        {
            AnsiConsole.MarkupLine(
                $"  [dim][[{agentName.EscapeMarkup()}]][/] " +
                $"[deepskyblue1]→ {toolName.EscapeMarkup()}[/] " +
                $"[grey]{args.EscapeMarkup()}[/]");
        }
    }

    /// <inheritdoc/>
    public void ReportToolResult(string agentName, string toolName, string result, bool isError = false)
    {
        var preview = result.Length > 160 ? result[..160] + "…" : result;
        var color   = isError ? "red" : "green3";
        var icon    = isError ? "✗" : "✓";

        lock (_lock)
        {
            AnsiConsole.MarkupLine(
                $"  [dim][[{agentName.EscapeMarkup()}]][/] " +
                $"[{color}]← {icon} {toolName.EscapeMarkup()}:[/] " +
                $"[dim]{preview.EscapeMarkup()}[/]");
        }
    }

    /// <inheritdoc/>
    public void ReportMaxIterationsReached(string agentName, int max)
    {
        lock (_lock)
        {
            AnsiConsole.MarkupLine(
                $"  [dim][[{agentName.EscapeMarkup()}]][/] " +
                $"[yellow]⚠ tool loop max iterations ({max}) reached — forcing final answer[/]");
        }
    }
}
