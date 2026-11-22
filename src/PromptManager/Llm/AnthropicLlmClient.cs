using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Options;

namespace PromptManager.Llm;

/// <summary>
/// Claude through the official Anthropic .NET SDK. Uses the beta Messages
/// endpoint only so that server-side refusal fallbacks can be switched on.
/// </summary>
public sealed class AnthropicLlmClient : ILlmClient
{
    public const string ProviderName = "Anthropic";

    private readonly AnthropicOptions _options;
    private readonly AnthropicClient? _client;

    public AnthropicLlmClient(IOptions<AnthropicOptions> options, IHttpClientFactory httpClientFactory)
    {
        _options = options.Value;

        // Fall back to the variable the SDK itself reads, so either way of
        // supplying the key works.
        var apiKey = string.IsNullOrWhiteSpace(_options.ApiKey)
            ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            : _options.ApiKey;

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _client = new AnthropicClient
            {
                ApiKey = apiKey,
                HttpClient = httpClientFactory.CreateClient(ProviderName),
                MaxRetries = _options.MaxRetries,
            };
        }
    }

    public string Provider => ProviderName;

    public bool IsConfigured => _client is not null;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        if (_client is null)
            throw new LlmException("No Anthropic API key is configured. Set Anthropic:ApiKey in user secrets or ANTHROPIC_API_KEY.");

        var parameters = new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = request.MaxTokens,
            OutputConfig = new BetaOutputConfig { Effort = ParseEffort(_options.Effort) },
            Messages = [new BetaUserMessageParam { Content = request.Prompt }],
        };

        if (_options.UseRefusalFallbacks)
        {
            parameters = parameters with
            {
                Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
                Fallbacks = new Default(),
            };
        }

        BetaMessage message;
        try
        {
            message = await _client.Beta.Messages.Create(parameters, ct);
        }
        catch (AnthropicRateLimitException ex)
        {
            throw new LlmException("Anthropic rate limit reached. Try again in a moment.", ex);
        }
        catch (AnthropicUnauthorizedException ex)
        {
            throw new LlmException("Anthropic rejected the API key.", ex);
        }
        catch (Anthropic5xxException ex)
        {
            throw new LlmException("Anthropic returned a server error. Try again later.", ex);
        }
        catch (AnthropicApiException ex)
        {
            // 4xx other than the above: usually a bad model id or parameter.
            throw new LlmException($"Anthropic rejected the request: {ex.Message}", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException("Could not reach the Anthropic API.", ex);
        }

        var text = string.Concat(message.Content
            .Select(b => b.TryPickText(out var t) ? t.Text : null)
            .Where(t => t is not null));

        var stopReason = message.StopReason?.Raw();
        var outcome = stopReason switch
        {
            "refusal" => LlmOutcome.Refused,
            "max_tokens" => LlmOutcome.Truncated,
            _ => LlmOutcome.Completed
        };

        string? note = null;
        if (outcome == LlmOutcome.Refused && message.StopDetails is { } details)
            note = $"Refused ({details.Category}): {details.Explanation}";

        var servedBy = message.Model.Raw();

        return new LlmResponse(
            Text: text,
            InputTokens: message.Usage.InputTokens,
            OutputTokens: message.Usage.OutputTokens,
            Outcome: outcome,
            StopReason: stopReason,
            Note: note,
            ServedByModel: servedBy == request.Model ? null : servedBy);
    }

    private static Effort ParseEffort(string value) => value.Trim().ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => Effort.High
    };
}
