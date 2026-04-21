using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArticleWriterAgents.Tools;

/// <summary>
/// Deterministic content-check tools for QA and content agents.
/// Provides word counting and section completeness verification.
/// </summary>
public class ArticleCheckTool
{
    /// <summary>
    /// Counts the total number of words in a block of text.
    /// </summary>
    [Description("Counts the total number of words in a text string. Use this to verify that an article section or the full article meets the required word count.")]
    public Task<string> CountWordsAsync(
        [Description("The text whose words should be counted")] string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult("Word count: 0");

        var count = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return Task.FromResult($"Word count: {count}");
    }

    /// <summary>
    /// Parses the article JSON and checks that every expected key point has a corresponding section.
    /// </summary>
    [Description("Parses an article JSON string and reports how many sections are present and which key points are missing a section. Use this to verify completeness before finalising the article.")]
    public Task<string> CheckSectionCountAsync(
        [Description("The full article JSON string (the 'sections' array will be inspected)")] string articleJson,
        [Description("The list of expected key point titles, comma-separated")] string expectedKeyPoints)
    {
        try
        {
            // Strip bare ASCII control characters (U+0000–U+001F) that are illegal inside
            // JSON string values per RFC 8259 §7. The three JSON whitespace chars
            // (tab 0x09, LF 0x0A, CR 0x0D) are preserved as they are valid outside strings.
            articleJson = Regex.Replace(articleJson, "[\x00-\x08\x0B\x0C\x0E-\x1F]", "");

            using var doc = JsonDocument.Parse(articleJson);
            var root = doc.RootElement;

            var actualTitles = new List<string>();
            if (root.TryGetProperty("sections", out var sections) &&
                sections.ValueKind == JsonValueKind.Array)
            {
                foreach (var sec in sections.EnumerateArray())
                    if (sec.TryGetProperty("title", out var t))
                        actualTitles.Add(t.GetString() ?? "");
            }

            var expected = expectedKeyPoints
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            // Check which expected points are not covered
            var missing = expected
                .Where(e => !actualTitles.Any(a =>
                    a.Contains(e, StringComparison.OrdinalIgnoreCase) ||
                    e.Contains(a, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var lines = new List<string>
            {
                $"Sections found: {actualTitles.Count} (expected: {expected.Count})",
                $"Section titles: {string.Join("; ", actualTitles)}"
            };

            if (missing.Count > 0)
                lines.Add($"⚠ Missing coverage for: {string.Join(", ", missing)}");
            else
                lines.Add("✓ All key points have matching sections.");

            return Task.FromResult(string.Join("\n", lines));
        }
        catch (JsonException ex)
        {
            return Task.FromResult($"Could not parse article JSON: {ex.Message}");
        }
    }

    /// <summary>Returns word-count as an <see cref="AIFunction"/>.</summary>
    public AIFunction CountWordsFunction() => AIFunctionFactory.Create(CountWordsAsync);

    /// <summary>Returns section-check as an <see cref="AIFunction"/>.</summary>
    public AIFunction CheckSectionCountFunction() => AIFunctionFactory.Create(CheckSectionCountAsync);
}
