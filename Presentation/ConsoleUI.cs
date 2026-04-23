using ArticleWriterAgents.Agents;
using ArticleWriterAgents.Models;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.Text.Json;

namespace ArticleWriterAgents.Presentation;

/// <summary>
/// All Spectre.Console rendering logic: menus, progress display, article results.
/// Kept separate from orchestration so Program.cs stays a thin entry point.
/// </summary>
internal static class ConsoleUI
{
    // ─── Progress bar layout ─────────────────────────────────────────────────

    private const int ProgressBarWidth = 48;
    private const double OrchestratorMinProgress = 10.0;
    private const double OrchestratorMaxProgress = 98.0;

    // ─── Public entry points ─────────────────────────────────────────────────

    public static void DisplayHeader()
    {
        AnsiConsole.Clear();

        AnsiConsole.Write(new Rule("[bold blue]🤖 AI Article Writer Agents[/]")
            .RuleStyle("blue"));

        AnsiConsole.Write(new Panel(
            new Markup(
                "[bold cyan]Welcome to the AI-powered article writing system![/]\n\n" +
                "[dim]This application uses 4 specialised AI agents:[/]\n" +
                "[green]📚 Research Agent[/] — gathers insights and expert quotes\n" +
                "[yellow]✍️  Content Agent[/] — writes professional articles\n" +
                "[red]🔍 Quality Agent[/] — comprehensive quality assessment\n" +
                "[blue]🎨 Presentation Agent[/] — beautiful HTML formatting"))
        {
            Header = new PanelHeader("[bold white]System Overview[/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = Style.Parse("dim blue")
        });

        AnsiConsole.WriteLine();
    }

    /// <summary>Collects all parameters for one article request via interactive prompts.</summary>
    public static async Task<ArticleRequest> CollectArticleRequestAsync(
        IServiceProvider services,
        ArticleGenerationConfig config)
    {
        var topic = AnsiConsole.Ask<string>("[bold cyan]🤖 What topic would you like to write about?[/]");

        var audience = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold yellow]👥 Who is your target audience?[/]")
                .AddChoices(config.AudienceChoices));

        var length = AnsiConsole.Prompt(
            new SelectionPrompt<ArticleLength>()
                .Title("[bold magenta]📏 How detailed should the article be?[/]")
                .AddChoices(ArticleLength.All)
                .UseConverter(l => l.Label));

        var tone = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold green]🎭 What tone should the article have?[/]")
                .AddChoices(config.ToneChoices));

        var keyPoints = await CollectKeyPointsAsync(topic, audience, services);

        return new ArticleRequest(topic, audience, length, tone, keyPoints.ToArray());
    }

    public static void DisplayRequestSummary(ArticleRequest request)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Blue)
            .AddColumn("[bold yellow]Property[/]")
            .AddColumn("[bold white]Value[/]");

        table.AddRow("[cyan]Topic[/]",           $"[white]{request.Topic.EscapeMarkup()}[/]");
        table.AddRow("[cyan]Target Audience[/]", $"[white]{request.TargetAudience.EscapeMarkup()}[/]");
        table.AddRow("[cyan]Word Count[/]",       $"[white]{request.Length.Label}[/]");
        table.AddRow("[cyan]Tone[/]",             $"[white]{request.ToneOfVoice.EscapeMarkup()}[/]");
        table.AddRow("[cyan]Key Points[/]",       $"[white]{string.Join(", ", request.KeyPoints).EscapeMarkup()}[/]");

        AnsiConsole.Write(new Panel(table)
        {
            Header = new PanelHeader("[bold green]📋 Article Specification[/]"),
            Border = BoxBorder.Rounded
        });

        AnsiConsole.WriteLine();
    }

    public static void DisplayResults(ArticleWorkflowResult result)
    {
        AnsiConsole.Write(new Panel(
            new Markup(
                $"[bold green]✅ Article successfully generated![/]\n\n" +
                $"[dim]Topic:[/]     [white]{result.OriginalRequest.Topic.EscapeMarkup()}[/]\n" +
                $"[dim]Revisions:[/] [white]{result.RevisionCount}[/]\n" +
                $"[dim]Completed:[/] [white]{result.CompletedAt:yyyy-MM-dd HH:mm:ss}[/]"))
        {
            Header = new PanelHeader("[bold green]🎉 Generation Complete[/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = Style.Parse("green")
        });

        AnsiConsole.WriteLine();

        if (result.QualityAssessment is not null)
            DisplayQualityAssessment(result.QualityAssessment, result.FinalQualityScore);

        DisplayArticlePreview(result);

        if (AnsiConsole.Confirm("[bold yellow]💾 Would you like to save the article to a file?[/]"))
            SaveArticleToFile(result);
    }

    // ─── Live progress helpers ───────────────────────────────────────────────

    /// <summary>
    /// Renders the two-line live progress widget: one progress bar + one thinking line.
    /// </summary>
    public static IRenderable BuildProgressDisplay(double progressPercent, string thinkingText)
    {
        var filled = Math.Clamp((int)(progressPercent / 100.0 * ProgressBarWidth), 0, ProgressBarWidth);
        var bar    = $"[yellow]{new string('█', filled)}[/][grey]{new string('░', ProgressBarWidth - filled)}[/]";
        var header = $" [bold white]✦ Generating Article[/]  {bar}  [bold white]{progressPercent,3:F0}%[/]";

        // Thinking line: visible italic with a leading arrow; kept blank when done so it vanishes cleanly
        var foot = string.IsNullOrEmpty(thinkingText)
            ? new Markup(" ")
            : new Markup($"  [deepskyblue1]▸[/] [italic skyblue1]{thinkingText.EscapeMarkup()}[/]");

        // Two blank lines below the thinking text create breathing room before the shell prompt
        return new Rows(new Markup(header), foot, new Markup(""), new Markup(""));
    }

    /// <summary>
    /// Maps raw orchestrator progress values (10→98) to display percentage (0→100).
    /// </summary>
    public static double NormaliseProgress(double raw)
        => Math.Clamp((raw - OrchestratorMinProgress) / (OrchestratorMaxProgress - OrchestratorMinProgress) * 100.0, 0, 100);

    // ─── Agent conversation tree ─────────────────────────────────────────────

    public static void DisplayAgentConversations(IReadOnlyList<AgentConversation> conversations)
    {
        if (!conversations.Any())
        {
            AnsiConsole.MarkupLine("\n[dim]No agent conversations to display.[/]");
            return;
        }

        AnsiConsole.Write(new Panel(BuildConversationTree(conversations))
        {
            Header = new PanelHeader("🤖 Agent Conversations"),
            Border = BoxBorder.Rounded,
            BorderStyle = Style.Parse("cyan")
        });

        AnsiConsole.WriteLine();
    }

    // ─── Private UI helpers ──────────────────────────────────────────────────

    private static async Task<List<string>> CollectKeyPointsAsync(
        string topic, string audience, IServiceProvider services)
    {
        AnsiConsole.MarkupLine("[bold blue]🗂️  Key Points & Focus Areas[/]");
        AnsiConsole.MarkupLine("[dim]Generating AI suggestions based on your topic…[/]");
        AnsiConsole.WriteLine();

        var suggested = await GenerateKeyPointSuggestionsAsync(topic, audience, services);

        if (!suggested.Any())
        {
            AnsiConsole.MarkupLine("[yellow]⚠️  Could not generate suggestions — switching to manual entry.[/]");
            return CollectKeyPointsManually();
        }

        AnsiConsole.MarkupLine("[bold green]🤖 AI Suggested Key Points:[/]");
        var points = new List<string>(suggested);

        while (true)
        {
            RenderKeyPoints(points);

            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold cyan]What would you like to do?[/]")
                    .AddChoices(
                        "✅ Accept all suggestions",
                        "➕ Add a new key point",
                        "✏️  Edit an existing key point",
                        "❌ Remove a key point",
                        "🔄 Start over with manual entry"));

            switch (action)
            {
                case "✅ Accept all suggestions":
                    AnsiConsole.MarkupLine($"\n[green]✅ Using {points.Count} key points.[/]");
                    return points;

                case "➕ Add a new key point":
                    var added = AnsiConsole.Ask<string>("[cyan]Enter your new key point:[/]").Trim();
                    if (!string.IsNullOrWhiteSpace(added))
                    {
                        points.Add(added);
                        AnsiConsole.MarkupLine("[green]✅ Added.[/]");
                    }
                    break;

                case "✏️  Edit an existing key point":
                    if (points.Count > 0)
                    {
                        var toEdit = AnsiConsole.Prompt(
                            new SelectionPrompt<string>()
                                .Title("[yellow]Which point?[/]")
                                .AddChoices(points));

                        var edited = AnsiConsole.Ask<string>("[cyan]New text:[/]", toEdit).Trim();
                        points[points.IndexOf(toEdit)] = edited;
                        AnsiConsole.MarkupLine("[green]✅ Updated.[/]");
                    }
                    break;

                case "❌ Remove a key point":
                    if (points.Count > 0)
                    {
                        var toRemove = AnsiConsole.Prompt(
                            new SelectionPrompt<string>()
                                .Title("[red]Which point to remove?[/]")
                                .AddChoices(points));

                        points.Remove(toRemove);
                        AnsiConsole.MarkupLine("[red]❌ Removed.[/]");
                    }
                    break;

                case "🔄 Start over with manual entry":
                    return CollectKeyPointsManually();
            }

            AnsiConsole.WriteLine();
        }
    }

    private static async Task<List<string>> GenerateKeyPointSuggestionsAsync(
        string topic, string audience, IServiceProvider services)
    {
        try
        {
            var agent = services.GetRequiredService<IResearchAgent>();
            var suggestions = await agent.SuggestKeyPointsAsync(topic, audience);
            return suggestions.Length > 0 ? [.. suggestions] : [];
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]⚠️  Error generating suggestions: {ex.Message.EscapeMarkup()}[/]");
            return [];
        }
    }

    private static List<string> CollectKeyPointsManually()
    {
        AnsiConsole.MarkupLine("[bold blue]🗂️  Manual Key Points[/]");
        AnsiConsole.MarkupLine("[dim]Press [yellow]Enter[/] on a blank line when done.[/]");
        AnsiConsole.WriteLine();

        var points = new List<string>();
        for (var i = 1; ; i++)
        {
            var input = AnsiConsole.Prompt(
                new TextPrompt<string>($"[cyan]Point {i}[/]").AllowEmpty());

            if (string.IsNullOrWhiteSpace(input)) break;
            points.Add(input.Trim());
        }

        if (points.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]⚠️  No points entered — using defaults.[/]");
            points.AddRange([
                "Current applications and trends",
                "Benefits and opportunities",
                "Challenges and considerations",
                "Future outlook and recommendations"
            ]);
        }

        RenderKeyPoints(points);
        return points;
    }

    private static void RenderKeyPoints(List<string> points)
    {
        AnsiConsole.MarkupLine("\n[bold white]Current Key Points:[/]");
        for (var i = 0; i < points.Count; i++)
            AnsiConsole.MarkupLine($"[dim]{i + 1}.[/] [white]{points[i].EscapeMarkup()}[/]");
        AnsiConsole.WriteLine();
    }

    private static void DisplayQualityAssessment(QualityAssessment assessment, double score)
    {
        var (ratingLabel, ratingColor) = ScoreRating(score);
        var sb = new System.Text.StringBuilder();

        sb.AppendLine();
        sb.AppendLine($"  [{ratingColor}]{score:F1} / 10[/]   [{ratingColor}]{ScoreStars(score)}  {ratingLabel.ToUpperInvariant()}[/]");
        sb.AppendLine();

        (string Name, double Value)[] metrics =
        [
            ("Clarity",      assessment.ClarityScore),
            ("Coherence",    assessment.CoherenceScore),
            ("Completeness", assessment.CompletenessScore),
            ("Accuracy",     assessment.AccuracyScore),
            ("Engagement",   assessment.EngagementScore),
            ("Grammar",      assessment.GrammarScore),
            ("Structure",    assessment.StructureScore),
            ("Relevance",    assessment.RelevanceScore),
        ];

        foreach (var (name, val) in metrics)
        {
            var (label, color) = ScoreRating(val);
            sb.AppendLine(
                $"  [bold white]{name,-13}[/]  [{color}]{ScoreBar(val)}[/]  [bold {color}]{val:F1}[/]  [dim]{label}[/]");
        }

        if (!string.IsNullOrWhiteSpace(assessment.Feedback))
        {
            sb.AppendLine();
            sb.AppendLine("  [dim]──────────────────────────────────────────────────────────[/]");
            sb.AppendLine($"  [dim italic]{assessment.Feedback.EscapeMarkup()}[/]");
        }

        sb.AppendLine();

        AnsiConsole.Write(new Panel(new Markup(sb.ToString()))
        {
            Header = new PanelHeader("[bold blue] ◆ Quality Assessment [/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = Style.Parse("blue"),
            Padding = new Padding(1, 0)
        });

        AnsiConsole.WriteLine();
    }

    private static void DisplayArticlePreview(ArticleWorkflowResult result)
    {
        string preview;
        try
        {
            var doc   = JsonDocument.Parse(result.FinalContent);
            var title = doc.RootElement.TryGetProperty("title",       out var t) ? t.GetString() ?? "" : "";
            var intro = doc.RootElement.TryGetProperty("introduction", out var i) ? i.GetString() ?? "" : "";
            var abst  = doc.RootElement.TryGetProperty("abstract",     out var a) ? a.GetString() ?? "" : "";
            var body  = string.IsNullOrEmpty(abst) ? intro : abst;
            var snippet = body.Length > 420 ? body[..420] + "…" : body;

            preview = string.IsNullOrEmpty(title)
                ? snippet.EscapeMarkup()
                : $"[bold white]{title.EscapeMarkup()}[/]\n\n{snippet.EscapeMarkup()}";
        }
        catch
        {
            var raw = result.FinalContent;
            preview = (raw.Length > 500 ? raw[..500] + "…" : raw).EscapeMarkup();
        }

        AnsiConsole.Write(new Panel(new Markup(preview))
        {
            Header = new PanelHeader("[bold magenta] 📄 Article Preview [/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = Style.Parse("magenta"),
            Padding = new Padding(1, 0)
        });

        AnsiConsole.WriteLine();
    }

    private static void SaveArticleToFile(ArticleWorkflowResult result)
    {
        try
        {
            var fileName = $"article_{DateTime.Now:yyyyMMdd_HHmmss}.html";
            var outputDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Output");
            Directory.CreateDirectory(outputDirectory);
            var filePath = Path.Combine(outputDirectory, fileName);

            File.WriteAllText(filePath, result.FormattedContent);
            AnsiConsole.MarkupLine($"[green]✅ Saved to: [link]{filePath.EscapeMarkup()}[/][/]");

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                });
                AnsiConsole.MarkupLine("[cyan]🌐 Opening in default browser…[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]⚠️  Could not auto-open: {ex.Message.EscapeMarkup()}[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]❌ Error saving file: {ex.Message.EscapeMarkup()}[/]");
        }
    }

    private static Tree BuildConversationTree(IReadOnlyList<AgentConversation> conversations)
    {
        var tree = new Tree("Agent Communication Flow");

        var groups = conversations
            .GroupBy(c => $"{c.FromAgent} → {c.ToAgent}")
            .OrderBy(g => conversations.First(c => $"{c.FromAgent} → {c.ToAgent}" == g.Key).Timestamp);

        foreach (var group in groups)
        {
            var node = tree.AddNode($"[bold cyan]{group.Key}[/]");

            foreach (var conv in group.OrderBy(c => c.Timestamp))
            {
                var icon = conv.ConversationType switch
                {
                    AgentConstants.ConversationTypeRequest  => "📝",
                    AgentConstants.ConversationTypeResponse => "✅",
                    AgentConstants.ConversationTypeFeedback => "💭",
                    _                                       => "💬"
                };

                var color = conv.ConversationType switch
                {
                    AgentConstants.ConversationTypeRequest  => AgentConstants.ColorYellow,
                    AgentConstants.ConversationTypeResponse => AgentConstants.ColorGreen,
                    AgentConstants.ConversationTypeFeedback => AgentConstants.ColorBlue,
                    _                                       => "white"
                };

                var preview = conv.Message.Length > 60
                    ? conv.Message[..57] + "…"
                    : conv.Message;

                node.AddNode(
                    $"{icon} [{color}]{conv.ConversationType}[/] at {conv.Timestamp:HH:mm:ss}\n" +
                    $"[dim]{preview.EscapeMarkup()}[/]");
            }
        }

        return tree;
    }

    // ─── Score rendering helpers ─────────────────────────────────────────────

    private static string ScoreBar(double score)
    {
        const int barLength = 22;
        var filled = Math.Clamp((int)Math.Round(score / 10.0 * barLength), 0, barLength);
        return new string('█', filled) + new string('░', barLength - filled);
    }

    private static string ScoreStars(double score) => score switch
    {
        >= 9.5 => "★★★★★",
        >= 8.5 => "★★★★½",
        >= 7.5 => "★★★★☆",
        >= 6.5 => "★★★☆☆",
        >= 5.5 => "★★☆☆☆",
        _      => "★☆☆☆☆"
    };

    private static (string label, string color) ScoreRating(double score) => score switch
    {
        >= 9.0 => ("Excellent",  "bold green"),
        >= 8.0 => ("Very Good",  "green"),
        >= 7.0 => ("Good",       "yellow"),
        >= 6.0 => ("Fair",       "orange1"),
        _      => ("Needs Work", "red")
    };
}
