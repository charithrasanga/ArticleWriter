using ArticleWriterAgents.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace ArticleWriterAgents.Tools;

/// <summary>
/// Wraps the Serper Google Search API so agents can retrieve real, current web results.
/// Register as a typed HttpClient via <c>AddHttpClient&lt;WebSearchTool&gt;</c>.
/// </summary>
public class WebSearchTool
{
    private readonly HttpClient _http;
    private readonly int _resultCount;
    private readonly ILogger<WebSearchTool> _logger;

    public WebSearchTool(HttpClient http, IOptions<SerperConfig> config, ILogger<WebSearchTool> logger)
    {
        _http        = http;
        _resultCount = config.Value.ResultCount;
        _logger      = logger;
    }

    /// <summary>
    /// Searches the web and returns titles, URLs, and snippets formatted for LLM consumption.
    /// </summary>
    [Description("Search the web for current information, research papers, and reliable sources on a topic. Returns real article titles, source URLs, and text snippets. Use this to find factual, up-to-date content before writing.")]
    public async Task<string> SearchWebAsync(
        [Description("The search query to look up, e.g. 'climate change effects on coral reefs 2024'")] string query)
    {
        _logger.LogDebug("WebSearchTool: querying Serper for '{Query}'", query);

        var payload = JsonSerializer.Serialize(new { q = query, num = _resultCount });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await _http.PostAsync("/search", content);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var formatted = FormatResults(json);

        _logger.LogDebug("WebSearchTool: returned {Chars} chars for '{Query}'", formatted.Length, query);
        return formatted;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string FormatResults(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var sb = new StringBuilder();

            if (doc.RootElement.TryGetProperty("organic", out var organic))
            {
                int i = 1;
                foreach (var result in organic.EnumerateArray())
                {
                    var title   = Str(result, "title");
                    var link    = Str(result, "link");
                    var snippet = Str(result, "snippet");
                    var date    = Str(result, "date");

                    sb.AppendLine($"[{i}] {title}");
                    sb.AppendLine($"    URL: {link}");
                    if (!string.IsNullOrWhiteSpace(date))
                        sb.AppendLine($"    Date: {date}");
                    sb.AppendLine($"    {snippet}");
                    sb.AppendLine();
                    i++;
                }
            }

            // Also include "answerBox" if Serper returned a quick answer
            if (doc.RootElement.TryGetProperty("answerBox", out var box))
            {
                var answer = Str(box, "answer");
                if (!string.IsNullOrWhiteSpace(answer))
                {
                    sb.Insert(0, $"Quick answer: {answer}\n\n");
                }
            }

            return sb.Length > 0 ? sb.ToString().TrimEnd() : "No search results found.";
        }
        catch
        {
            return "Search results could not be parsed.";
        }
    }

    private static string Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) ? v.GetString() ?? "" : "";
}
