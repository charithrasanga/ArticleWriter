using ArticleWriterAgents.Utils;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.ComponentModel;

namespace ArticleWriterAgents.Tools;

/// <summary>
/// Exposes URL reachability checking as an <see cref="AIFunction"/> so a research agent
/// can verify sources before including them in the output.
/// </summary>
public class UrlValidatorTool
{
    private readonly UrlValidator _validator;
    private readonly ILogger<UrlValidatorTool> _logger;

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
