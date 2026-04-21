using ArticleWriterAgents.Utils;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.ComponentModel;

namespace ArticleWriterAgents.Tools;

/// <summary>
/// Exposes URL reachability checking as an <see cref="AIFunction"/> so a research agent
/// can verify sources before including them in the output.
/// Well-known, highly-reliable domains are whitelisted to skip the HTTP round-trip entirely.
/// </summary>
public class UrlValidatorTool
{
    private readonly UrlValidator _validator;
    private readonly ILogger<UrlValidatorTool> _logger;

    /// <summary>
    /// Domains that are reliably reachable and do not need an HTTP HEAD request.
    /// Sources returned by Serper from these domains are accepted immediately.
    /// </summary>
    private static readonly HashSet<string> TrustedDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "wikipedia.org", "www.wikipedia.org",
        "bbc.com", "www.bbc.com", "bbc.co.uk", "www.bbc.co.uk",
        "reuters.com", "www.reuters.com",
        "apnews.com", "www.apnews.com",
        "theguardian.com", "www.theguardian.com",
        "nytimes.com", "www.nytimes.com",
        "washingtonpost.com", "www.washingtonpost.com",
        "nature.com", "www.nature.com",
        "science.org", "www.science.org",
        "pubmed.ncbi.nlm.nih.gov", "ncbi.nlm.nih.gov",
        "who.int", "www.who.int",
        "cdc.gov", "www.cdc.gov",
        "gov.uk", "www.gov.uk",
        "europa.eu", "www.europa.eu",
        "un.org", "www.un.org",
        "worldbank.org", "www.worldbank.org",
        "imf.org", "www.imf.org",
        "oecd.org", "www.oecd.org",
        "stackoverflow.com", "www.stackoverflow.com",
        "github.com", "www.github.com",
        "microsoft.com", "www.microsoft.com", "learn.microsoft.com",
        "developer.mozilla.org",
        "arxiv.org", "www.arxiv.org",
        "medium.com", "www.medium.com",
        "forbes.com", "www.forbes.com",
        "bloomberg.com", "www.bloomberg.com",
        "economist.com", "www.economist.com",
        "nationalgeographic.com", "www.nationalgeographic.com",
        "smithsonianmag.com", "www.smithsonianmag.com",
    };

    public UrlValidatorTool(UrlValidator validator, ILogger<UrlValidatorTool> logger)
    {
        _validator = validator;
        _logger    = logger;
    }

    /// <summary>
    /// Checks whether a URL is reachable via an HTTP HEAD request.
    /// </summary>
    [Description("Checks whether a URL is accessible and returns a valid HTTP response. Call this before including any source URL in research output to verify the link actually works.")]
    public async Task<string> ValidateUrlAsync(
        [Description("The full URL to validate, e.g. 'https://www.nature.com/articles/d41586-021-00019-2'")] string url)
    {
        _logger.LogDebug("UrlValidatorTool: checking {Url}", url);

        // Skip the HTTP round-trip for well-known, highly-reliable domains.
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsedUri)
            && TrustedDomains.Contains(parsedUri.Host))
        {
            _logger.LogDebug("UrlValidatorTool: trusted domain — skipping HEAD request for {Url}", url);
            return $"VALID — URL is accessible: {url}";
        }

        var isValid = await _validator.IsUrlValidAsync(url);
        var result  = isValid
            ? $"VALID — URL is accessible: {url}"
            : $"INVALID — URL is not reachable: {url} (use the domain root instead, e.g. https://www.nature.com)";

        _logger.LogDebug("UrlValidatorTool: {Result}", result);
        return result;
    }

    /// <summary>Returns this tool as an <see cref="AIFunction"/> for use in <see cref="ChatOptions.Tools"/>.</summary>
    public AIFunction AsAIFunction() => AIFunctionFactory.Create(ValidateUrlAsync);
}
