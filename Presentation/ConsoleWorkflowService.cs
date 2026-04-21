using ArticleWriterAgents.Agents;
using ArticleWriterAgents.Models;
using Microsoft.Extensions.Options;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ArticleWriterAgents.Presentation;

public interface IConsoleWorkflowService
{
    Task RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns the interactive Spectre.Console workflow so Program.cs can remain a thin composition root.
/// </summary>
public sealed class ConsoleWorkflowService : IConsoleWorkflowService
{
    private readonly IServiceProvider _services;
    private readonly ArticleWorkflowOrchestrator _orchestrator;
    private readonly ArticleGenerationConfig _config;

    public ConsoleWorkflowService(
        IServiceProvider services,
        ArticleWorkflowOrchestrator orchestrator,
        IOptions<ArticleGenerationConfig> config)
    {
        _services = services;
        _orchestrator = orchestrator;
        _config = config.Value;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ConsoleUI.DisplayHeader();

        var request = await ConsoleUI.CollectArticleRequestAsync(_services, _config);
        ConsoleUI.DisplayRequestSummary(request);

        var result = await ExecuteWithProgressAsync(request, cancellationToken);
        ConsoleUI.DisplayResults(result);
    }

    private async Task<ArticleWorkflowResult> ExecuteWithProgressAsync(
        ArticleRequest request,
        CancellationToken cancellationToken)
    {
        var conversations = new List<AgentConversation>();
        AnsiConsole.WriteLine();

        double pct = 0;
        string thinking = "Preparing...";

        ArticleWorkflowResult? result = null;

        await AnsiConsole.Live(ConsoleUI.BuildProgressDisplay(pct, thinking))
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .StartAsync(async ctx =>
            {
                var progress = new Progress<(string phase, double progress)>(report =>
                {
                    pct = ConsoleUI.NormaliseProgress(report.progress);
                    thinking = GetThinkingDescription(report.phase);
                    ctx.UpdateTarget(ConsoleUI.BuildProgressDisplay(pct, thinking));
                });

                result = await _orchestrator.ProcessArticleRequestAsync(
                    request, progress, conversations, cancellationToken);

                // Vanish the thinking line once complete.
                ctx.UpdateTarget(ConsoleUI.BuildProgressDisplay(100, ""));
            });

        AnsiConsole.WriteLine();
        ConsoleUI.DisplayAgentConversations(conversations);
        return result!;
    }

    private static string GetThinkingDescription(string phase) => phase.ToLowerInvariant() switch
    {
        var p when p.Contains("research") =>
            "Searching the web, evaluating sources, and extracting key facts relevant to the topic...",

        var p when p.Contains("content") =>
            "Drafting the article: introduction, sections, subsections, and conclusion...",

        var p when p.Contains("quality") =>
            "Running quality checks on clarity, coherence, accuracy, engagement, grammar, and structure...",

        var p when p.Contains("revis") =>
            "Rewriting sections based on quality feedback to improve score and readability...",

        var p when p.Contains("presentation") =>
            "Rendering final HTML: layout, typography, images, and responsive styling...",

        var p when p.Contains("final") =>
            "Completing final checks and packaging the article for output...",

        _ => $"{phase}..."
    };
}
