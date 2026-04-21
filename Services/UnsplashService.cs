using System.Text.Json;
using System.Text.Json.Serialization;
using ArticleWriterAgents.Models;
using Microsoft.Extensions.Options;

namespace ArticleWriterAgents.Services;

/// <summary>
/// Resolves keyword-based image queries to real Unsplash CDN photo URLs via the Unsplash API.
/// Falls back to the source.unsplash.com redirect service when no Access Key is configured.
/// </summary>
public interface IUnsplashService
{
    /// <summary>
    /// Returns a direct CDN image URL for the given <paramref name="query"/>.
    /// Never throws — returns a fallback URL on any error.
    /// </summary>
    Task<string> ResolveImageUrlAsync(string query, string size, CancellationToken cancellationToken = default);
}

public sealed class UnsplashService : IUnsplashService
{
    private static readonly string FallbackBase = "https://source.unsplash.com";

    private readonly HttpClient _http;
    private readonly ImageConfig _config;

    public UnsplashService(HttpClient http, IOptions<ImageConfig> options)
    {
        _http   = http;
        _config = options.Value;
    }

    /// <inheritdoc/>
    public async Task<string> ResolveImageUrlAsync(
        string query,
        string size,
        CancellationToken cancellationToken = default)
    {
        // When no Access Key is configured, fall back to the anonymous source endpoint.
        if (string.IsNullOrWhiteSpace(_config.AccessKey))
            return BuildFallbackUrl(size, query);

        try
        {
            var apiUrl = _config.BuildApiUrl(size, query);
            using var response = await _http.GetAsync(apiUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return BuildFallbackUrl(size, query);

            var json    = await response.Content.ReadAsStringAsync(cancellationToken);
            var photo   = JsonSerializer.Deserialize<UnsplashPhoto>(json);
            var url     = photo?.Urls?.Regular ?? photo?.Urls?.Full ?? photo?.Urls?.Small;

            return string.IsNullOrWhiteSpace(url) ? BuildFallbackUrl(size, query) : url;
        }
        catch
        {
            return BuildFallbackUrl(size, query);
        }
    }

    // ------------------------------------------------------------------
    // Fallback: Unsplash Source (no auth required, may be rate-limited)
    // ------------------------------------------------------------------

    private static string BuildFallbackUrl(string size, string query)
        => $"{FallbackBase}/{Uri.EscapeDataString(size)}/?{Uri.EscapeDataString(query)}";

    // ------------------------------------------------------------------
    // Private DTO
    // ------------------------------------------------------------------

    private sealed class UnsplashPhoto
    {
        [JsonPropertyName("urls")]
        public UnsplashUrls? Urls { get; init; }
    }

    private sealed class UnsplashUrls
    {
        [JsonPropertyName("full")]
        public string? Full { get; init; }

        [JsonPropertyName("regular")]
        public string? Regular { get; init; }

        [JsonPropertyName("small")]
        public string? Small { get; init; }
    }
}
