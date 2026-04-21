using ArticleWriterAgents.Models;
using ArticleWriterAgents.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace ArticleWriterAgents.Agents;

/// <summary>
/// Writes initial article drafts and produces targeted revisions based on QA feedback.
/// Uses a higher temperature (0.8) to encourage creative, engaging prose.
/// </summary>
public class ContentCreationAgent : BaseAgent, IContentCreationAgent
{
    private readonly ArticleCheckTool _articleCheck;

    // 12+ sections × 3 subsections of JSON needs a large output budget
    protected override int MaxOutputTokens => 16_000;
    // Higher temperature for varied, engaging writing
    protected override float Temperature => 0.8f;

    public ContentCreationAgent(
        IChatClient chatClient,
        ILogger<ContentCreationAgent> logger,
        ArticleCheckTool articleCheck,
        IToolCallReporter toolCallReporter)
        : base(chatClient, logger, toolCallReporter)
    {
        _articleCheck = articleCheck;
    }

    /// <inheritdoc/>
    public async Task<string> CreateAsync(
        ArticleRequest request,
        string researchData,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Creating initial article draft for topic: {Topic}", request.Topic);

        var systemPrompt = @"
You are a professional content creation agent. You write high-quality, well-sourced articles in a strict JSON format.

MANDATORY ARTICLE STRUCTURE — every response must contain exactly these fields:
{
  ""title"": ""<compelling, SEO-friendly title>"",
  ""headerImageQuery"": ""<3–5 word image search phrase representing the article topic (e.g. 'ancient pyramids human civilization')>"",
  ""abstract"": ""<2–3 sentence executive summary—what the reader will learn and why it matters>"",
  ""tableOfContents"": [
    ""1. <translated 'Introduction'>"",
    ""2. <Section 1 heading in article language>"",
    ""3. <Section 2 heading in article language>"",
    ""N. <translated 'Key Takeaways'>"",
    ""N+1. <translated 'Conclusion'>""
  ],
  ""introduction"": ""<hook + context + preview of all sections, 150–200 words>"",
  ""sections"": [
    {
      ""title"": ""<section heading IN THE ARTICLE LANGUAGE>"",
      ""imageQuery"": ""<3–5 word image search phrase specific to this section's subject matter>"",
      ""summary"": ""<1 sentence: the core claim or insight of this section>"",
      ""subsections"": [
        {
          ""heading"": ""<sub-topic heading IN THE ARTICLE LANGUAGE>"",
          ""content"": ""<2–3 focused paragraphs: context → evidence/quote from research → real-world example → implication>""
        }
      ]
    }
  ],
  ""keyTakeaways"": [""<concise actionable insight>""],
  ""conclusion"": ""<synthesis + future outlook + call to action, 150–200 words>"",
  ""sources"": [""<Publication Name> — <URL from research data only>""],
  ""labels"": {
    ""keyTakeaways"": ""<'Key Takeaways' translated into the article language>"",
    ""conclusion"": ""<'Conclusion' translated into the article language>"",
    ""references"": ""<'References' translated into the article language>"",
    ""contents"": ""<'Contents' translated into the article language>"",
    ""backToTop"": ""<'↑ Back to top' translated into the article language>"",
    ""inDepth"": ""<'In Depth' translated into the article language>"",
    ""published"": ""<'Published' translated into the article language>""
  }
}

CONTENT RULES:
- LANGUAGE: Detect the language of the topic/request and write ALL text in that language. This applies to
  EVERY string field: title, abstract, introduction, sections[].title, sections[].summary,
  subsections[].heading, subsections[].content, tableOfContents items, keyTakeaways, conclusion,
  and labels values. Only imageQuery and headerImageQuery must remain in English (for image search).
- Create exactly ONE section per key point provided, in the ORDER they are given — do not merge, skip, or reorder
- Each section MUST have 2–3 subsections that break the topic into clear sub-topics
- imageQuery and headerImageQuery MUST be concise, visual, and specific (e.g. 'deep sea exploration submarine' not 'science')
- tableOfContents lists: Introduction, each section title, Key Takeaways, Conclusion — all in the article language
- keyTakeaways: 5–7 concise, actionable items
- SOURCES: copy verbatim ONLY from the research data provided — do NOT invent new URLs or titles
- Plain prose in all JSON string values — no markdown, no asterisks, no hash characters
- Return ONLY the JSON object — no preamble, no code fences, no explanation

TOOL USAGE:
After generating the article JSON, call check_section_count_async passing the full JSON and the expected key points
as a comma-separated string. If any sections are missing, add them before returning your final answer.";


        var userMessage = $@"
Topic:           {request.Topic}
Target Audience: {request.TargetAudience}
Article Length:  {request.Length.Label} (target ≈{request.RequiredLength} words, distributed evenly across sections)
Tone:            {request.ToneOfVoice}

Key Points — create one section per item, in this exact order:
{string.Join("\n", request.KeyPoints.Select((p, i) => $"  {i + 1}. {p}"))}

SECTION COUNT RULE: sections[] MUST contain exactly {request.KeyPoints.Length} entries — one per key point above.
If content is long, write tighter subsections rather than omitting any section. Missing sections are a hard failure.

Research data (use for facts, quotes, and inline citations):
{researchData}

Return ONLY the JSON object. No preamble, no markdown fences.";

        var tools = new[] { _articleCheck.CheckSectionCountFunction() };
        var response = await CallWithToolsAsync(systemPrompt, userMessage, tools, cancellationToken);
        _logger.LogInformation("Initial article draft created for topic: {Topic}", request.Topic);
        return response;
    }

    /// <inheritdoc/>
    public async Task<string> ReviseAsync(
        ArticleRequest request,
        string currentContent,
        string qualityFeedback,
        string[] revisionSuggestions,
        int[]? weakSectionIndices,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Revising article based on QA feedback for topic: {Topic}", request.Topic);

        // If QA identified specific weak sections, do a targeted revision (much cheaper).
        // Fall back to full revision when no indices were provided or the array is empty.
        if (weakSectionIndices is { Length: > 0 })
        {
            return await ReviseTargetedSectionsAsync(
                request, currentContent, qualityFeedback,
                revisionSuggestions, weakSectionIndices, cancellationToken);
        }

        return await ReviseFullArticleAsync(
            request, currentContent, qualityFeedback, revisionSuggestions, cancellationToken);
    }

    // ── Targeted (section-level) revision ────────────────────────────────────

    private async Task<string> ReviseTargetedSectionsAsync(
        ArticleRequest request,
        string currentContent,
        string qualityFeedback,
        string[] revisionSuggestions,
        int[] weakSectionIndices,
        CancellationToken cancellationToken)
    {
        // Extract the sections that need revision from the full article JSON.
        List<(int Index, string SectionJson)> weakSections = ExtractSectionsByIndex(currentContent, weakSectionIndices);

        if (weakSections.Count == 0)
        {
            // Could not parse sections — fall back to full revision.
            _logger.LogWarning("Could not extract weak sections; falling back to full revision.");
            return await ReviseFullArticleAsync(request, currentContent, qualityFeedback, revisionSuggestions, cancellationToken);
        }

        _logger.LogInformation("Targeted revision: rewriting {Count} of {Total} sections (indices: {Indices})",
            weakSections.Count, request.KeyPoints.Length, string.Join(", ", weakSectionIndices));

        var systemPrompt = @"
You are a professional content editor performing a targeted section revision.
You will receive specific article sections that need improvement, along with QA feedback.

REVISION RULES:
- Address EVERY revision suggestion item — no exceptions.
- Improve only what is needed; do not change the section title or imageQuery unless they are flagged.
- Maintain existing citations; add more only from the original research data.
- Do NOT change the section structure — keep the same number of subsections.

MANDATORY OUTPUT FORMAT — return ONLY a JSON array of the revised sections (same structure as input):
[
  {
    ""title"": ""..."",
    ""imageQuery"": ""..."",
    ""summary"": ""..."",
    ""subsections"": [ { ""heading"": ""..."", ""content"": ""..."" } ]
  }
]

LANGUAGE RULE: Preserve the article language throughout. Do NOT translate to English.
Only imageQuery must remain in English.

Return ONLY the JSON array — no preamble, no markdown fences.";

        var suggestionsFormatted = string.Join("\n", revisionSuggestions.Select((s, i) => $"  {i + 1}. {s}"));
        var sectionsJson = System.Text.Json.JsonSerializer.Serialize(
            weakSections.Select(s => System.Text.Json.JsonDocument.Parse(s.SectionJson).RootElement).ToArray(),
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        var userMessage = $@"
Revise the following article sections based on the QA feedback.

ORIGINAL REQUEST:
Topic: {request.Topic}
Target Audience: {request.TargetAudience}
Tone: {request.ToneOfVoice}

QUALITY FEEDBACK:
{qualityFeedback}

SPECIFIC REVISION SUGGESTIONS (address each one):
{suggestionsFormatted}

SECTIONS TO REVISE (indices {string.Join(", ", weakSectionIndices)}):
{sectionsJson}

Return ONLY the JSON array with the revised sections (same count and order as input).";

        var response = await CallAsync(systemPrompt, userMessage, cancellationToken);

        // Merge the revised sections back into the full article JSON.
        var merged = MergeRevisedSections(currentContent, weakSections.Select(s => s.Index).ToArray(), response);
        _logger.LogInformation("Targeted revision completed for topic: {Topic}", request.Topic);
        return merged;
    }

    // ── Full-article revision (fallback) ──────────────────────────────────────

    private async Task<string> ReviseFullArticleAsync(
        ArticleRequest request,
        string currentContent,
        string qualityFeedback,
        string[] revisionSuggestions,
        CancellationToken cancellationToken)
    {
        var systemPrompt = @"
You are a professional content editor. Revise the article draft to address every piece of QA feedback.

REVISION RULES:
- Address EVERY item in the revision suggestions list — no exceptions
- Improve only the weak areas; preserve strong sections as-is
- Maintain all existing citations; add more where helpful, but ONLY use URLs from the original research data
- Do NOT rewrite from scratch — targeted improvements only

MANDATORY OUTPUT FORMAT (identical structure to the input draft, preserving ALL fields including labels):
{
  ""title"": ""..."",
  ""headerImageQuery"": ""..."",
  ""abstract"": ""..."",
  ""tableOfContents"": [...],
  ""introduction"": ""..."",
  ""sections"": [
    {
      ""title"": ""..."",
      ""imageQuery"": ""..."",
      ""summary"": ""..."",
      ""subsections"": [ { ""heading"": ""..."", ""content"": ""..."" } ]
    }
  ],
  ""keyTakeaways"": [...],
  ""conclusion"": ""..."",
  ""sources"": [""<Publication Name> — <URL from research data only>""],
  ""labels"": { ""keyTakeaways"": ""..."", ""conclusion"": ""..."", ""references"": ""..."", ""contents"": ""..."", ""backToTop"": ""..."", ""inDepth"": ""..."", ""published"": ""..."" }
}

LANGUAGE RULE: Preserve the article language. Do NOT translate any content to English during revision.
Only imageQuery and headerImageQuery must remain in English.

Return ONLY the revised JSON object — no preamble, no markdown fences.

TOOL USAGE:
After revising, call check_section_count_async with the revised JSON and the original key points to confirm
no sections were accidentally removed. Fix any missing sections before returning.";

        var suggestionsFormatted = string.Join("\n", revisionSuggestions.Select((s, i) => $"  {i + 1}. {s}"));

        var userMessage = $@"
Please revise the following article draft based on the quality assessment feedback.

ORIGINAL REQUEST:
Topic: {request.Topic}
Target Audience: {request.TargetAudience}
Article Length: {request.Length.Label} (target ≈{request.RequiredLength} words)
Tone: {request.ToneOfVoice}

QUALITY FEEDBACK SUMMARY:
{qualityFeedback}

SPECIFIC REVISION SUGGESTIONS (address each one):
{suggestionsFormatted}

CURRENT ARTICLE DRAFT:
{currentContent}

Instructions:
1. Address each revision suggestion explicitly
2. Improve the specific areas flagged in the feedback
3. Preserve strong sections — do not rewrite unnecessarily
4. Maintain all source citations
5. Keep the same JSON structure";

        var reviseTools = new[] { _articleCheck.CheckSectionCountFunction() };
        var response = await CallWithToolsAsync(systemPrompt, userMessage, reviseTools, cancellationToken);
        _logger.LogInformation("Full article revision completed for topic: {Topic}", request.Topic);
        return response;
    }

    // ── Section extraction / merging helpers ──────────────────────────────────

    /// <summary>
    /// Extracts the JSON element for each requested section index from the article JSON.
    /// Returns only the successfully extracted (index, raw JSON) pairs.
    /// </summary>
    private static List<(int Index, string SectionJson)> ExtractSectionsByIndex(
        string articleJson, int[] indices)
    {
        var result = new List<(int, string)>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(articleJson);
            if (!doc.RootElement.TryGetProperty("sections", out var sections)
                || sections.ValueKind != System.Text.Json.JsonValueKind.Array)
                return result;

            var sectionArray = sections.EnumerateArray().ToList();
            foreach (var idx in indices)
            {
                if (idx >= 0 && idx < sectionArray.Count)
                    result.Add((idx, sectionArray[idx].GetRawText()));
            }
        }
        catch (System.Text.Json.JsonException) { /* return whatever we got */ }
        return result;
    }

    /// <summary>
    /// Parses the LLM-returned revised sections array and splices them back
    /// into their original positions in the full article JSON string.
    /// Falls back to returning <paramref name="originalJson"/> unchanged if parsing fails.
    /// </summary>
    private string MergeRevisedSections(string originalJson, int[] targetIndices, string revisedSectionsJson)
    {
        try
        {
            revisedSectionsJson = ExtractJsonArrayFromText(revisedSectionsJson.Trim());

            using var origDoc    = System.Text.Json.JsonDocument.Parse(originalJson);
            using var revisedDoc = System.Text.Json.JsonDocument.Parse(revisedSectionsJson);

            if (revisedDoc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                _logger.LogWarning("Revised sections response was not a JSON array; keeping original.");
                return originalJson;
            }

            var revisedArray  = revisedDoc.RootElement.EnumerateArray().ToList();
            var origSections  = origDoc.RootElement.GetProperty("sections").EnumerateArray().ToList();

            // Splice each revised section in at its target index.
            for (int i = 0; i < targetIndices.Length && i < revisedArray.Count; i++)
                origSections[targetIndices[i]] = revisedArray[i];

            // Rebuild the full JSON with the patched sections array.
            var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = false };
            using var stream = new System.IO.MemoryStream();
            using var writer = new System.Text.Json.Utf8JsonWriter(stream);

            writer.WriteStartObject();
            foreach (var prop in origDoc.RootElement.EnumerateObject())
            {
                if (prop.Name == "sections")
                {
                    writer.WritePropertyName("sections");
                    writer.WriteStartArray();
                    foreach (var sec in origSections)
                        sec.WriteTo(writer);
                    writer.WriteEndArray();
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
            writer.Flush();

            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to merge revised sections; returning original article.");
            return originalJson;
        }
    }

    private static string ExtractJsonArrayFromText(string text)
    {
        var start = text.IndexOf('[');
        var end   = text.LastIndexOf(']');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }
}
