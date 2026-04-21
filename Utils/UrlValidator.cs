using Microsoft.Extensions.Logging;
using System.Net;

namespace ArticleWriterAgents.Utils;

/// <summary>
/// Utility class for validating URLs and providing fallback options
/// </summary>
public class UrlValidator
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<UrlValidator> _logger;

    public UrlValidator(HttpClient httpClient, ILogger<UrlValidator> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Validates if a URL is accessible
    /// </summary>
    /// <param name="url">URL to validate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if URL is accessible, false otherwise</returns>
    public async Task<bool> IsUrlValidAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            request.Headers.Add("User-Agent", "ArticleWriter-UrlValidator/1.0");
            
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to validate URL: {Url}", url);
            return false;
        }
    }

    /// <summary>
    /// Validates multiple URLs concurrently
    /// </summary>
    /// <param name="urls">URLs to validate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary with URL and validation result</returns>
    public async Task<Dictionary<string, bool>> ValidateUrlsAsync(IEnumerable<string> urls, CancellationToken cancellationToken = default)
    {
        var validationTasks = urls.Select(async url => new
        {
            Url = url,
            IsValid = await IsUrlValidAsync(url, cancellationToken)
        });

        var results = await Task.WhenAll(validationTasks);
        return results.ToDictionary(r => r.Url, r => r.IsValid);
    }

    /// <summary>
    /// Gets a valid placeholder image URL from Picsum Photos (Lorem Picsum)
    /// </summary>
    /// <param name="width">Image width</param>
    /// <param name="height">Image height</param>
    /// <param name="seed">Optional seed for consistent images</param>
    /// <returns>Valid placeholder image URL</returns>
    public static string GetPlaceholderImageUrl(int width = 800, int height = 500, string? seed = null)
    {
        // Using Picsum Photos (Lorem Picsum) which provides reliable placeholder images
        if (!string.IsNullOrEmpty(seed))
        {
            return $"https://picsum.photos/seed/{seed}/{width}/{height}";
        }
        return $"https://picsum.photos/{width}/{height}";
    }

    /// <summary>
    /// Gets a topic-specific placeholder image URL with grayscale option
    /// </summary>
    /// <param name="width">Image width</param>
    /// <param name="height">Image height</param>
    /// <param name="grayscale">Whether to use grayscale</param>
    /// <param name="blur">Blur level (1-10)</param>
    /// <returns>Themed placeholder image URL</returns>
    public static string GetThemedPlaceholderImageUrl(int width = 800, int height = 500, bool grayscale = false, int? blur = null)
    {
        var url = $"https://picsum.photos/{width}/{height}";
        
        var parameters = new List<string>();
        if (grayscale) parameters.Add("grayscale");
        if (blur.HasValue && blur.Value >= 1 && blur.Value <= 10) parameters.Add($"blur={blur.Value}");
        
        if (parameters.Any())
        {
            url += "?" + string.Join("&", parameters);
        }
        
        return url;
    }

    /// <summary>
    /// Provides fallback URLs for common domains that might be down
    /// </summary>
    /// <param name="originalUrl">Original URL that failed</param>
    /// <returns>Alternative URL or placeholder</returns>
    public static string GetFallbackUrl(string originalUrl)
    {
        if (string.IsNullOrWhiteSpace(originalUrl))
            return GetPlaceholderImageUrl();

        // Check if it's an image URL
        var imageExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg" };
        if (imageExtensions.Any(ext => originalUrl.ToLowerInvariant().Contains(ext)))
        {
            return GetPlaceholderImageUrl();
        }

        // For non-image URLs, try Internet Archive Wayback Machine as fallback
        if (Uri.TryCreate(originalUrl, UriKind.Absolute, out var uri))
        {
            return $"https://web.archive.org/web/{uri}";
        }

        return originalUrl; // Return original if we can't determine a good fallback
    }

    /// <summary>
    /// Gets well-known, reliable source URLs for research
    /// </summary>
    /// <returns>List of reliable domain patterns</returns>
    public static List<string> GetReliableSourceDomains()
    {
        return new List<string>
        {
            "wikipedia.org",
            "britannica.com",
            "reuters.com",
            "bbc.com",
            "cnn.com",
            "npr.org",
            "pbs.org",
            "nature.com",
            "science.org",
            "sciencedirect.com",
            "pubmed.ncbi.nlm.nih.gov",
            "scholar.google.com",
            "arxiv.org",
            "jstor.org",
            "ieee.org",
            "acm.org",
            "springer.com",
            "wiley.com",
            "taylor&francis.com",
            "cambridge.org",
            "oxford.com",
            "harvard.edu",
            "mit.edu",
            "stanford.edu",
            "berkeley.edu",
            "government sites (.gov)",
            "education sites (.edu)",
            "organization sites (.org)"
        };
    }
}
