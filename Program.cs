using ArticleWriterAgents.Agents;
using ArticleWriterAgents.Models;
using ArticleWriterAgents.Presentation;
using ArticleWriterAgents.Services;
using ArticleWriterAgents.Tools;using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ArticleWriterAgents;

static class Program
{
    static async Task Main(string[] args)
    {
        EnsureDefaultEnvironment();

        var host = BuildHost(args);

        try
        {
            ConsoleUI.DisplayHeader();

            var orchestrator = host.Services.GetRequiredService<ArticleWorkflowOrchestrator>();
            var config       = host.Services.GetRequiredService<IOptions<ArticleGenerationConfig>>().Value;

            var request = await ConsoleUI.CollectArticleRequestAsync(host.Services, config);
            ConsoleUI.DisplayRequestSummary(request);

            var result = await ExecuteWithProgressAsync(orchestrator, request);
            ConsoleUI.DisplayResults(result);
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            Environment.Exit(1);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ─── Host setup ───────────────────────────────────────────────────────────

    static IHost BuildHost(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureLogging(logging =>
            {
                // Spectre.Console owns stdout — route logs to the debug sink only
                // so progress bars and menus are never interrupted.
                logging.ClearProviders();
                logging.AddDebug();
            })
            .ConfigureServices((ctx, services) =>
            {
                var config = ctx.Configuration;

                var endpoint       = config["AzureOpenAI:Endpoint"]       ?? throw new InvalidOperationException("AzureOpenAI:Endpoint is required.");
                var deploymentName = config["AzureOpenAI:DeploymentName"] ?? throw new InvalidOperationException("AzureOpenAI:DeploymentName is required.");
                var apiKey         = config["AzureOpenAI:ApiKey"];

                services.AddSingleton<IChatClient>(_ =>
                {
                    var azureClient = string.IsNullOrWhiteSpace(apiKey)
                        ? new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
                        : new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));

                    return azureClient.GetChatClient(deploymentName).AsIChatClient();
                });

                services.Configure<ArticleGenerationConfig>(config.GetSection("ArticleGeneration"));
                services.Configure<ImageConfig>(config.GetSection("Images"));
                services.Configure<SerperConfig>(config.GetSection("Serper"));

                services.AddHttpClient<Utils.UrlValidator>(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(10);
                    client.DefaultRequestHeaders.Add("User-Agent", "ArticleWriter-UrlValidator/1.0");
                });

                services.AddHttpClient<WebSearchTool>(client =>
                {
                    client.BaseAddress = new Uri("https://google.serper.dev");
                    client.DefaultRequestHeaders.Add("X-API-KEY", config["Serper:ApiKey"] ?? "");
                    client.Timeout = TimeSpan.FromSeconds(15);
                });

                services.AddHttpClient<IUnsplashService, UnsplashService>(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(10);
                    client.DefaultRequestHeaders.Add("Accept-Version", "v1");
                });

                services.AddSingleton<Utils.UrlValidator>();
                services.AddSingleton<UrlValidatorTool>();
                services.AddSingleton<ArticleCheckTool>();
                services.AddSingleton<IToolCallReporter, ConsoleToolCallReporter>();

                // Agents are stateless between calls — singletons are safe.
                services.AddSingleton<IResearchAgent,         ResearchAgent>();
                services.AddSingleton<IContentCreationAgent,  ContentCreationAgent>();
                services.AddSingleton<IQualityAssuranceAgent, QualityAssuranceAgent>();
                services.AddSingleton<IPresentationAgent,     PresentationAgent>();
                services.AddSingleton<ArticleWorkflowOrchestrator>();
            })
            .Build();

    // ─── Workflow execution ───────────────────────────────────────────────────

    static async Task<ArticleWorkflowResult> ExecuteWithProgressAsync(
        ArticleWorkflowOrchestrator orchestrator,
        ArticleRequest request)
    {
        var conversations = new List<AgentConversation>();
        AnsiConsole.WriteLine();

        double  pct      = 0;
        string  thinking = "Preparing…";

        ArticleWorkflowResult? result = null;

        await AnsiConsole.Live(ConsoleUI.BuildProgressDisplay(pct, thinking))
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .StartAsync(async ctx =>
            {
                var progress = new Progress<(string phase, double progress)>(report =>
                {
                    pct      = ConsoleUI.NormaliseProgress(report.progress);
                    thinking = GetThinkingDescription(report.phase);
                    ctx.UpdateTarget(ConsoleUI.BuildProgressDisplay(pct, thinking));
                });

                result = await orchestrator.ProcessArticleRequestAsync(request, progress, conversations);

                // Vanish the thinking line once complete (matches GitHub Copilot behaviour)
                ctx.UpdateTarget(ConsoleUI.BuildProgressDisplay(100, ""));
            });

        AnsiConsole.WriteLine();
        ConsoleUI.DisplayAgentConversations(conversations);
        return result!;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    static void EnsureDefaultEnvironment()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")) &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")))
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");
        }
    }

    static string GetThinkingDescription(string phase) => phase.ToLower() switch
    {
        var p when p.Contains("research") =>
            "Searching the web, evaluating sources, and extracting key facts relevant to the topic…",

        var p when p.Contains("content") =>
            "Drafting the article — writing the introduction, body sections, subsections, and conclusion…",

        var p when p.Contains("quality") =>
            "Running quality checks on clarity, coherence, accuracy, engagement, grammar, and structure…",

        var p when p.Contains("revis") =>
            "Rewriting sections based on quality feedback to improve score and readability…",

        var p when p.Contains("presentation") =>
            "Rendering the final HTML — applying layout, typography, images, and responsive styling…",

        var p when p.Contains("final") =>
            "Completing final checks and packaging the article for output…",

        _ => $"{phase}…"
    };
}
