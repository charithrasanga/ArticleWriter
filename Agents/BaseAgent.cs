using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ArticleWriterAgents.Agents;

/// <summary>
/// Base class for all agents. Provides an <see cref="IChatClient"/> abstraction,
/// per-agent configurable options, and built-in retry with exponential back-off that
/// honours the <c>Retry-After</c> header for HTTP 429 responses.
/// </summary>
public abstract class BaseAgent
{
    protected readonly IChatClient _chatClient;
    protected readonly ILogger _logger;
    private readonly IToolCallReporter? _toolCallReporter;

    // Subclasses override these to tune the model call for their specific role.
    // e.g. research = low temperature (factual), content creation = higher (creative).
    protected virtual int MaxOutputTokens => 4_000;
    protected virtual float Temperature => 0.7f;

    /// <summary>Maximum tool-call iterations before forcing a final answer without tools.</summary>
    protected virtual int MaxToolIterations => 10;

    private static readonly IReadOnlySet<int> TransientStatusCodes =
        new HashSet<int> { 429, 500, 502, 503 };

    protected BaseAgent(IChatClient chatClient, ILogger logger, IToolCallReporter? toolCallReporter = null)
    {
        _chatClient          = chatClient          ?? throw new ArgumentNullException(nameof(chatClient));
        _logger              = logger              ?? throw new ArgumentNullException(nameof(logger));
        _toolCallReporter    = toolCallReporter;
    }

    /// <summary>
    /// Calls the underlying <see cref="IChatClient"/> with automatic retry for transient errors.
    /// </summary>
    protected async Task<string> CallAsync(
        string systemPrompt,
        string userMessage,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userMessage)
        };

        var options = new ChatOptions
        {
            MaxOutputTokens = MaxOutputTokens,
            Temperature = Temperature
        };

        var response = await ExecuteWithRetryAsync(
            ct => _chatClient.GetResponseAsync(messages, options, ct),
            cancellationToken);

        return response.Text ?? string.Empty;
    }

    /// <summary>
    /// Calls the model in a tool-use loop. The model may invoke any of the supplied
    /// <paramref name="tools"/> zero or more times; the loop continues until the model
    /// returns a plain text response or <see cref="MaxToolIterations"/> is reached.
    /// </summary>
    protected async Task<string> CallWithToolsAsync(
        string systemPrompt,
        string userMessage,
        IReadOnlyList<AIFunction> tools,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userMessage)
        };

        var options = new ChatOptions
        {
            MaxOutputTokens = MaxOutputTokens,
            Temperature     = Temperature,
            Tools           = [.. tools]
        };

        for (int iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            var response = await ExecuteWithRetryAsync(
                ct => _chatClient.GetResponseAsync(messages, options, ct),
                cancellationToken);

            // Collect any function-call requests from the model's response
            var functionCalls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            if (functionCalls.Count == 0)
            {
                // No tool calls — the model produced its final answer
                return response.Text ?? string.Empty;
            }

            var agentName = GetType().Name;

            _logger.LogDebug(
                "Tool-call loop iteration {Iteration}: {Count} tool call(s) requested",
                iteration + 1, functionCalls.Count);
            _toolCallReporter?.ReportIteration(agentName, iteration + 1, functionCalls.Count);

            // Append the assistant turn (contains the tool-call requests) to history
            messages.AddRange(response.Messages);

            // Execute each tool and append its result
            foreach (var call in functionCalls)
            {
                var argsJson = call.Arguments is not null
                    ? JsonSerializer.Serialize(call.Arguments)
                    : "{}";
                _logger.LogDebug("→ Tool: {Name}({Args})", call.Name, argsJson);
                _toolCallReporter?.ReportToolCall(agentName, call.Name, argsJson);

                var fn = tools.FirstOrDefault(t =>
                    string.Equals(t.Name, call.Name, StringComparison.OrdinalIgnoreCase));

                string resultText;
                if (fn is null)
                {
                    resultText = $"Error: unknown tool '{call.Name}'";
                    _logger.LogWarning("Unknown tool requested: {Name}", call.Name);
                }
                else
                {
                    try
                    {
                        var args = call.Arguments is not null
                            ? new AIFunctionArguments(call.Arguments)
                            : null;
                        var raw = await fn.InvokeAsync(args, cancellationToken);
                        resultText = raw?.ToString() ?? "null";
                    }
                    catch (Exception ex)
                    {
                        resultText = $"Error: {ex.Message}";
                        _logger.LogWarning(ex, "Tool {Name} threw an exception", call.Name);
                    }
                }

                var preview = resultText.Length > 300
                    ? resultText[..300] + "…"
                    : resultText;
                _logger.LogDebug("← {Name} result: {Result}", call.Name, preview);
                _toolCallReporter?.ReportToolResult(agentName, call.Name, resultText,
                    isError: resultText.StartsWith("Error:", StringComparison.OrdinalIgnoreCase));

                messages.Add(new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(call.CallId, resultText)]));
            }
        }

        // Exceeded max iterations — ask for a final answer without further tool calls
        _logger.LogWarning(
            "Tool-call loop reached max iterations ({Max}); requesting final answer without tools",
            MaxToolIterations);
        _toolCallReporter?.ReportMaxIterationsReached(GetType().Name, MaxToolIterations);

        var finalOptions = new ChatOptions
        {
            MaxOutputTokens = MaxOutputTokens,
            Temperature     = Temperature
        };
        var finalResponse = await ExecuteWithRetryAsync(
            ct => _chatClient.GetResponseAsync(messages, finalOptions, ct),
            cancellationToken);

        return finalResponse.Text ?? string.Empty;
    }

    // ── Retry infrastructure ─────────────────────────────────────────────────

    private async Task<ChatResponse> ExecuteWithRetryAsync(
        Func<CancellationToken, Task<ChatResponse>> operation,
        CancellationToken cancellationToken,
        int maxAttempts = 3)
    {
        Exception? lastException = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await operation(cancellationToken);
            }
            catch (Azure.RequestFailedException ex)
                when (TransientStatusCodes.Contains(ex.Status) && attempt < maxAttempts)
            {
                lastException = ex;
                var delay = ResolveRetryDelay(ex, attempt);
                _logger.LogWarning(
                    "Transient HTTP {Status} on attempt {Attempt}/{Max}; retrying in {DelaySeconds:F1}s",
                    ex.Status, attempt, maxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts)
            {
                lastException = ex;
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _logger.LogWarning(
                    "Network error on attempt {Attempt}/{Max}; retrying in {DelaySeconds:F1}s: {Message}",
                    attempt, maxAttempts, delay.TotalSeconds, ex.Message);
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw lastException!;
    }

    /// <summary>
    /// Resolves the retry delay, honouring the <c>Retry-After</c> response header when present
    /// (common for HTTP 429 Too Many Requests).
    /// </summary>
    private static TimeSpan ResolveRetryDelay(Azure.RequestFailedException ex, int attempt)
    {
        var raw = ex.GetRawResponse();
        if (raw is not null &&
            raw.Headers.TryGetValue("retry-after", out var headerValue) &&
            int.TryParse(headerValue, out var retryAfterSeconds))
        {
            return TimeSpan.FromSeconds(retryAfterSeconds);
        }

        return TimeSpan.FromSeconds(Math.Pow(2, attempt));
    }
}

