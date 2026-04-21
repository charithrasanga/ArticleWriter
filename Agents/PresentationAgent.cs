using ArticleWriterAgents.Models;
using ArticleWriterAgents.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArticleWriterAgents.Agents;

/// <summary>
/// Transforms finalized article JSON into a fully-styled, responsive HTML document.
/// HTML is built entirely in C# — no AI call — so every section is always rendered.
/// </summary>
public class PresentationAgent : BaseAgent, IPresentationAgent
{
    protected override int MaxOutputTokens => 16_000;
    protected override float Temperature   => 0.7f;

    private readonly ImageConfig      _imageConfig;
    private readonly IUnsplashService _unsplash;

    public PresentationAgent(
        IChatClient chatClient,
        ILogger<PresentationAgent> logger,
        IOptions<ImageConfig> imageOptions,
        IUnsplashService unsplash,
        IToolCallReporter toolCallReporter)
        : base(chatClient, logger, toolCallReporter)
    {
        _imageConfig = imageOptions.Value;
        _unsplash    = unsplash;
    }

    /// <inheritdoc/>
    public async Task<string> FormatAsync(
        ArticleRequest request,
        string content,
        QualityAssessment? qualityAssessment,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting presentation formatting for topic: {Topic}", request.Topic);

        var headerImgSize  = _imageConfig.HeaderSize;
        var sectionImgSize = _imageConfig.SectionSize;

        // Pre-resolve image queries to real Unsplash CDN URLs.
        var (headerImageUrl, sectionImageUrls) = await ResolveImageUrlsAsync(
            content, headerImgSize, sectionImgSize, cancellationToken);

        // Build HTML entirely in C# so every section is always rendered,
        // regardless of article length.
        var html = BuildHtml(content, request, qualityAssessment, headerImageUrl, sectionImageUrls);

        _logger.LogInformation("Presentation formatting completed for topic: {Topic}", request.Topic);
        return html;
    }

    // ── C# HTML Builder ──────────────────────────────────────────────────────

    private static string BuildHtml(
        string articleJson,
        ArticleRequest request,
        QualityAssessment? qa,
        string headerImageUrl,
        List<(string Title, string Url)> sectionImageUrls)
    {
        // ── Parse JSON ───────────────────────────────────────────────────────
        string title        = request.Topic;
        string abstractText = "";
        string introduction = "";
        string conclusion   = "";
        var    toc          = new List<string>();
        var    sections     = new List<(string Title, string Summary, List<(string Heading, string Content)> Subsections)>();
        var    takeaways    = new List<string>();
        var    sources      = new List<string>();

        // Localizable UI labels — defaults to English, overridden by labels in the article JSON
        string labelKeyTakeaways = "Key Takeaways";
        string labelConclusion   = "Conclusion";
        string labelReferences   = "References";
        string labelContents     = "Contents";
        string labelBackToTop    = "\u2191 Back to top";
        string labelInDepth      = "In Depth";
        string labelPublished    = "Published";

        try
        {
            using var doc = JsonDocument.Parse(articleJson);
            var root = doc.RootElement;

            title        = GetStr(root, "title",        title);
            abstractText = GetStr(root, "abstract",     abstractText);
            introduction = GetStr(root, "introduction", introduction);
            conclusion   = GetStr(root, "conclusion",   conclusion);

            if (root.TryGetProperty("tableOfContents", out var tocEl) && tocEl.ValueKind == JsonValueKind.Array)
                foreach (var item in tocEl.EnumerateArray())
                    toc.Add(item.GetString() ?? "");

            if (root.TryGetProperty("sections", out var secArr) && secArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var sec in secArr.EnumerateArray())
                {
                    var secTitle   = GetStr(sec, "title",   "");
                    var secSummary = GetStr(sec, "summary", "");
                    var subsections = new List<(string Heading, string Content)>();

                    if (sec.TryGetProperty("subsections", out var subArr) && subArr.ValueKind == JsonValueKind.Array)
                        foreach (var sub in subArr.EnumerateArray())
                            subsections.Add((GetStr(sub, "heading", ""), GetStr(sub, "content", "")));

                    sections.Add((secTitle, secSummary, subsections));
                }
            }

            if (root.TryGetProperty("keyTakeaways", out var ktEl) && ktEl.ValueKind == JsonValueKind.Array)
                foreach (var item in ktEl.EnumerateArray())
                    takeaways.Add(item.GetString() ?? "");

            if (root.TryGetProperty("sources", out var srcEl) && srcEl.ValueKind == JsonValueKind.Array)
                foreach (var item in srcEl.EnumerateArray())
                    sources.Add(item.GetString() ?? "");

            if (root.TryGetProperty("labels", out var labelsEl) && labelsEl.ValueKind == JsonValueKind.Object)
            {
                labelKeyTakeaways = GetStr(labelsEl, "keyTakeaways", labelKeyTakeaways);
                labelConclusion   = GetStr(labelsEl, "conclusion",   labelConclusion);
                labelReferences   = GetStr(labelsEl, "references",   labelReferences);
                labelContents     = GetStr(labelsEl, "contents",     labelContents);
                labelBackToTop    = GetStr(labelsEl, "backToTop",    labelBackToTop);
                labelInDepth      = GetStr(labelsEl, "inDepth",      labelInDepth);
                labelPublished    = GetStr(labelsEl, "published",    labelPublished);
            }
        }
        catch (JsonException)
        {
            // If JSON is malformed, fall back to rendering whatever we have.
        }

        // Estimate reading time (average 200 wpm).
        int words = EstimateWords(introduction, sections, conclusion);
        int readMins = Math.Max(1, (int)Math.Round(words / 200.0));
        string readTime = $"{readMins} min read";
        string generatedDate = DateTime.Now.ToString("MMMM d, yyyy");

        // ── Build HTML ───────────────────────────────────────────────────────
        var sb = new StringBuilder(65_536);

        sb.AppendLine($"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="UTF-8">
              <meta name="viewport" content="width=device-width, initial-scale=1.0">
              <title>{H(title)}</title>
              <script src="https://cdn.tailwindcss.com"></script>
              <link href="https://fonts.googleapis.com/css2?family=Playfair+Display:ital,wght@0,400;0,700;1,400&family=Inter:wght@300;400;500;600&display=swap" rel="stylesheet">
              <style>
            """);
        // Non-interpolated block: CSS curly braces must not be escaped.
        sb.AppendLine("""
                body { font-family: 'Inter', sans-serif; }
                .playfair { font-family: 'Playfair Display', serif; }
                .dropcap::first-letter {
                  float: left; font-size: 5rem; line-height: 1; margin-right: 0.5rem;
                  color: #4f46e5; font-family: 'Playfair Display', serif; font-weight: 700;
                }
                .toc-link.active { color: #4f46e5; font-weight: 600; }
              </style>
            </head>
            <body class="bg-gray-50 text-gray-800">
            """);

        // ① Reading progress bar
        sb.AppendLine("""
              <div id="progress-bar" style="position:fixed;top:0;left:0;height:3px;width:0%;background:linear-gradient(90deg,#6366f1,#8b5cf6);z-index:9999;transition:width .1s ease"></div>
            """);

        // ② Sticky nav bar (hidden until scroll)
        sb.AppendLine($"""
              <nav id="site-nav" class="fixed top-0 left-0 right-0 bg-slate-900 z-50 px-6 py-3 flex items-center justify-between transition-all duration-300" style="transform:translateY(-100%)">
                <span class="text-white text-sm font-medium truncate max-w-xs md:max-w-lg playfair">{H(title)}</span>
                <a href="#top" class="text-indigo-300 hover:text-white text-sm transition-colors">{H(labelBackToTop)}</a>
              </nav>
            """);

        // ③ Hero section
        sb.AppendLine($"""
              <div id="top"></div>
              <header class="relative h-screen overflow-hidden">
                <img src="{H(headerImageUrl)}" alt="{H(title)}" class="absolute inset-0 w-full h-full object-cover">
                <div class="absolute inset-0" style="background:linear-gradient(to top,rgba(0,0,0,0.82) 0%,rgba(0,0,0,0.4) 50%,rgba(0,0,0,0.2) 100%)"></div>
                <div class="relative z-10 flex flex-col items-center justify-end h-full pb-20 px-6 text-center">
                  <span class="inline-block bg-indigo-600 text-white text-xs font-semibold uppercase tracking-widest px-3 py-1 rounded-full mb-6">{H(labelInDepth)}</span>
                  <h1 class="playfair text-5xl md:text-7xl font-bold text-white leading-tight max-w-4xl mb-4">{H(title)}</h1>
                  <p class="text-slate-300 text-xl italic max-w-2xl mb-6">{H(abstractText)}</p>
                  <div class="flex items-center gap-3 text-slate-400 text-sm">
                    <span>⏱ {H(readTime)}</span>
                    <span>·</span>
                    <span>{H(request.TargetAudience)}</span>
                  </div>
                </div>
              </header>
            """);

        // ④ Main two-column layout
        sb.AppendLine("""
              <main class="max-w-7xl mx-auto px-6 py-16">
                <div class="lg:grid lg:grid-cols-[1fr_280px] gap-12">
                  <!-- LEFT COLUMN -->
                  <div>
            """);

        // A. Introduction card
        if (!string.IsNullOrWhiteSpace(introduction))
        {
            sb.AppendLine($"""
                      <div class="bg-white rounded-2xl shadow-sm p-8 mb-12">
                        <p class="text-gray-700 leading-relaxed text-[17px] dropcap">{H(introduction)}</p>
                      </div>
                """);
        }

        // B. Body sections
        for (int i = 0; i < sections.Count; i++)
        {
            var (secTitle, secSummary, subsections) = sections[i];
            string sectionNum = (i + 1).ToString("D2");
            string sectionSlug = Slug(secTitle);
            string imgUrl = i < sectionImageUrls.Count ? sectionImageUrls[i].Url : "";

            sb.AppendLine($"""
                      <section id="{sectionSlug}" class="mb-16">
                        <div class="flex items-center gap-4 mb-6">
                          <span class="playfair text-5xl font-bold text-indigo-100 select-none">{sectionNum}</span>
                          <h2 class="playfair text-3xl font-bold text-slate-900 leading-tight">{H(secTitle)}</h2>
                        </div>
                """);

            if (!string.IsNullOrWhiteSpace(imgUrl))
            {
                sb.AppendLine($"""
                          <img src="{H(imgUrl)}" alt="{H(secTitle)}" loading="lazy"
                               class="w-full rounded-2xl object-cover mb-3" style="max-height:480px">
                          <p class="text-xs text-slate-400 italic mb-8">{H(secTitle)}</p>
                    """);
            }

            if (!string.IsNullOrWhiteSpace(secSummary))
            {
                sb.AppendLine($"""
                          <blockquote class="border-l-4 border-indigo-500 pl-6 my-8 italic text-xl text-slate-600 leading-relaxed">
                            {H(secSummary)}
                          </blockquote>
                    """);
            }

            foreach (var (heading, subContent) in subsections)
            {
                sb.AppendLine($"""
                          <div class="mb-8">
                            <h3 class="text-xl font-semibold text-slate-800 mb-3 flex items-center gap-2">
                              <span class="w-2 h-2 rounded-full bg-indigo-500 inline-block flex-shrink-0"></span>
                              {H(heading)}
                            </h3>
                            <p class="text-gray-700 leading-relaxed text-[17px]">{H(subContent)}</p>
                          </div>
                    """);
            }

            sb.AppendLine("      </section>");

            // Decorative divider after every 2nd section (but not after the last)
            if ((i + 1) % 2 == 0 && i < sections.Count - 1)
            {
                sb.AppendLine("""
                          <div class="flex items-center gap-4 my-12">
                            <div class="flex-1 h-px bg-slate-200"></div>
                            <span class="text-slate-300 text-xl">✦</span>
                            <div class="flex-1 h-px bg-slate-200"></div>
                          </div>
                    """);
            }
        }

        // C. Key takeaways
        if (takeaways.Count > 0)
        {
            sb.AppendLine("""
                      <div class="bg-indigo-50 rounded-2xl p-8 mb-12 border border-indigo-100">
                        <h2 class="playfair text-2xl font-bold text-indigo-900 mb-6">{H(labelKeyTakeaways)}</h2>
                """);
            for (int k = 0; k < takeaways.Count; k++)
            {
                string border = k < takeaways.Count - 1 ? " border-b border-indigo-100 pb-4 mb-4" : "";
                sb.AppendLine($"""
                          <div class="flex items-start gap-3{border}">
                            <span class="text-indigo-500 mt-1 flex-shrink-0">✦</span>
                            <p class="text-slate-700 text-[17px]">{H(takeaways[k])}</p>
                          </div>
                    """);
            }
            sb.AppendLine("      </div>");
        }

        // D. Conclusion card
        if (!string.IsNullOrWhiteSpace(conclusion))
        {
            sb.AppendLine($"""
                      <div class="bg-slate-50 rounded-2xl p-8 mb-12 border border-slate-200">
                        <h2 class="playfair text-2xl font-bold text-slate-900 mb-4">{H(labelConclusion)}</h2>
                        <p class="text-gray-700 text-[17px] leading-relaxed">{H(conclusion)}</p>
                      </div>
                """);
        }

        // E. References
        if (sources.Count > 0)
        {
            sb.AppendLine("""
                      <div class="bg-white rounded-2xl shadow-sm p-8 mb-12">
                        <h2 class="text-xl font-semibold text-slate-700 mb-4">{H(labelReferences)}</h2>
                        <ol class="list-decimal list-inside space-y-2">
                """);
            foreach (var source in sources)
            {
                // Linkify URLs
                var linked = Regex.Replace(
                    H(source),
                    @"https?://\S+",
                    m => $"""<a href="{m.Value}" class="text-indigo-600 hover:underline" target="_blank" rel="noopener noreferrer">{m.Value}</a>""");
                sb.AppendLine($"""          <li class="text-sm text-slate-600">{linked}</li>""");
            }
            sb.AppendLine("        </ol>");
            sb.AppendLine("      </div>");
        }

        // Close left column
        sb.AppendLine("    </div>"); // </div> left col

        // RIGHT SIDEBAR
        sb.AppendLine("""
                  <!-- RIGHT SIDEBAR -->
                  <aside>
                    <div class="lg:sticky lg:top-20 space-y-6">
            """);

        // F. Table of contents
        var tocItems = toc.Count > 0 ? toc : sections.Select(s => s.Title).ToList();
        if (tocItems.Count > 0)
        {
            sb.AppendLine("""
                          <div class="bg-white rounded-2xl shadow-sm p-6 border border-slate-100">
                            <p class="text-xs font-semibold uppercase tracking-widest text-slate-400 mb-4">{H(labelContents)}</p>
                            <nav>
                """);
            for (int t = 0; t < tocItems.Count; t++)
            {
                string tocSlug = t < sections.Count ? Slug(sections[t].Title) : Slug(tocItems[t]);
                sb.AppendLine($"""
                              <a href="#{tocSlug}" class="toc-link block text-sm text-slate-600 hover:text-indigo-600 transition-colors py-1.5 border-b border-slate-100 last:border-0">
                                <span class="text-slate-300 mr-2 text-xs">{(t + 1).ToString("D2")}</span>{H(tocItems[t])}
                              </a>
                    """);
            }
            sb.AppendLine("          </nav>");
            sb.AppendLine("        </div>");
        }

        // G. Meta card
        sb.AppendLine($"""
                      <div class="bg-gradient-to-br from-indigo-600 to-purple-600 rounded-2xl p-6 text-white">
                        <div class="w-10 h-10 bg-white rounded-full flex items-center justify-center text-2xl mb-3">🤖</div>
                        <p class="font-semibold text-sm">Generated by AI Article Writer</p>
                        <p class="text-indigo-200 text-sm mt-1">{H(request.Topic)}</p>
                        <p class="text-indigo-300 text-xs mt-2">{H(generatedDate)}</p>
                      </div>
                """);

        sb.AppendLine("    </div>"); // sticky wrapper
        sb.AppendLine("  </aside>");
        sb.AppendLine("</div>"); // grid
        sb.AppendLine("</main>");

        // ⑤ Footer
        sb.AppendLine($"""
              <footer class="bg-slate-900 text-white py-16">
                <div class="max-w-4xl mx-auto px-6 text-center">
                  <h2 class="playfair italic text-3xl mb-2">{H(title)}</h2>
                  <p class="text-slate-400 text-sm">{H(labelPublished)} · {H(generatedDate)} · {H(readTime)}</p>
                  <div class="border-t border-slate-700 mt-8 pt-6">
                    <p class="text-slate-500 text-xs">© {DateTime.Now.Year} AI Article Writer · All rights reserved.</p>
                  </div>
                </div>
              </footer>
            """);

        // ⑥ JavaScript — non-interpolated to avoid brace conflicts
        sb.AppendLine("""
              <script>
              (function() {
                // Reading progress bar
                const bar = document.getElementById('progress-bar');
                const nav = document.getElementById('site-nav');
                const heroH = document.querySelector('header')?.offsetHeight ?? 600;

                window.addEventListener('scroll', () => {
                  const scrolled = window.scrollY;
                  const total = document.documentElement.scrollHeight - window.innerHeight;
                  if (bar && total > 0) bar.style.width = (scrolled / total * 100) + '%';

                  // Sticky nav: show after hero
                  if (nav) nav.style.transform = scrolled > heroH ? 'translateY(0)' : 'translateY(-100%)';

                  // TOC scroll spy
                  const sections = document.querySelectorAll('section[id]');
                  let current = '';
                  sections.forEach(sec => {
                    if (scrolled >= sec.offsetTop - 120) current = sec.id;
                  });
                  document.querySelectorAll('.toc-link').forEach(a => {
                    a.classList.toggle('active', a.getAttribute('href') === '#' + current);
                  });
                }, { passive: true });
              })();
              </script>
            """);

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    // ── HTML helpers ──────────────────────────────────────────────────────────

    /// <summary>HTML-encodes a string so it is safe to embed in attributes and text nodes.</summary>
    private static string H(string? text) =>
        string.IsNullOrEmpty(text) ? "" : WebUtility.HtmlEncode(text);

    /// <summary>Creates a URL-safe anchor slug from a heading string.</summary>
    private static string Slug(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "section";
        var s = text.ToLowerInvariant();
        s = Regex.Replace(s, @"[^a-z0-9\s-]", "");
        s = Regex.Replace(s, @"\s+", "-");
        s = s.Trim('-');
        return string.IsNullOrEmpty(s) ? "section" : s;
    }

    /// <summary>Reads a string property from a <see cref="JsonElement"/>, returning the fallback if absent.</summary>
    private static string GetStr(JsonElement el, string prop, string fallback) =>
        el.TryGetProperty(prop, out var v) ? (v.GetString() ?? fallback) : fallback;

    /// <summary>Estimates total word count across all article sections for reading-time calculation.</summary>
    private static int EstimateWords(
        string introduction,
        List<(string Title, string Summary, List<(string Heading, string Content)> Subsections)> sections,
        string conclusion)
    {
        int count = CountWords(introduction) + CountWords(conclusion);
        foreach (var (_, summary, subsections) in sections)
        {
            count += CountWords(summary);
            foreach (var (_, content) in subsections)
                count += CountWords(content);
        }
        return count;
    }

    private static int CountWords(string? text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;


    /// <summary>
    /// Parses the article JSON to collect all image queries, then resolves them to real
    /// Unsplash CDN URLs in parallel.  Returns the header URL and an ordered list of
    /// (sectionTitle, resolvedUrl) pairs.
    /// </summary>
    private async Task<(string HeaderUrl, List<(string Title, string Url)> SectionUrls)>
        ResolveImageUrlsAsync(
            string articleJson,
            string headerSize,
            string sectionSize,
            CancellationToken cancellationToken)
    {
        string headerQuery = _imageConfig.HeaderSize; // sensible fallback
        var    sectionQueries = new List<(string Title, string Query)>();

        try
        {
            using var doc = JsonDocument.Parse(articleJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("headerImageQuery", out var hq))
                headerQuery = hq.GetString() ?? headerQuery;

            if (root.TryGetProperty("sections", out var sections)
                && sections.ValueKind == JsonValueKind.Array)
            {
                foreach (var section in sections.EnumerateArray())
                {
                    var title = section.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var query = section.TryGetProperty("imageQuery", out var q) ? q.GetString() ?? title : title;
                    sectionQueries.Add((title, query));
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Could not parse article JSON to extract image queries; using fallback images.");
        }

        // Fire all requests in parallel to minimise latency.
        var headerTask = _unsplash.ResolveImageUrlAsync(headerQuery, headerSize, cancellationToken);
        var sectionTasks = sectionQueries
            .Select(sq => _unsplash.ResolveImageUrlAsync(sq.Query, sectionSize, cancellationToken))
            .ToList();

        await Task.WhenAll(sectionTasks.Prepend(headerTask));

        var resolvedSections = sectionQueries
            .Zip(sectionTasks, (sq, task) => (sq.Title, Url: task.Result))
            .ToList();

        return (headerTask.Result, resolvedSections);
    }
}
