using ArticleWriterAgents.Agents;
using ArticleWriterAgents.Models;
using ArticleWriterAgents.Presentation;
using ArticleWriterAgents.Services;
using ArticleWriterAgents.Tools;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace ArticleWriterAgents;

static class Program
{
    static async Task Main(string[] args)
    {
        EnsureDefaultEnvironment();

        var host = BuildHost(args);

        try
        {
            var workflow = host.Services.GetRequiredService<IConsoleWorkflowService>();
            await workflow.RunAsync();
        }
        catch (Exception ex)
        {
            // AnsiConsole.WriteException has a known crash on certain stack-frame formats.
            // Write a safe plain-text fallback instead.
            try { AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything); }
            catch { Console.Error.WriteLine($"[ERROR] {ex.GetType().Name}: {ex.Message}"); }
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

                var endpoint = config["AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("AzureOpenAI:Endpoint is required.");
                var deploymentName = config["AzureOpenAI:DeploymentName"] ?? throw new InvalidOperationException("AzureOpenAI:DeploymentName is required.");
                var apiKey = config["AzureOpenAI:ApiKey"];

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
                services.AddSingleton<IConsoleWorkflowService, ConsoleWorkflowService>();

                // Agents are stateless between calls — singletons are safe.
                services.AddSingleton<IResearchAgent, ResearchAgent>();
                services.AddSingleton<IContentCreationAgent, ContentCreationAgent>();
                services.AddSingleton<IQualityAssuranceAgent, QualityAssuranceAgent>();
                services.AddSingleton<IPresentationAgent, PresentationAgent>();
                services.AddSingleton<ArticleWorkflowOrchestrator>();
            })
            .Build();

    // ─── Helpers ─────────────────────────────────────────────────────────────

    static void EnsureDefaultEnvironment()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")) &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")))
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");
        }
    }

}
