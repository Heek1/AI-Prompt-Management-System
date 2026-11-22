using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PromptManager.Llm;

namespace PromptManager.Tests.Llm;

/// <summary>
/// Drives the real SDK client against a stub HTTP handler, so request shape
/// and response parsing are checked without a network call or an API key.
/// </summary>
public class AnthropicLlmClientTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static AnthropicLlmClient Client(StubHandler handler, AnthropicOptions? options = null)
    {
        options ??= new AnthropicOptions();
        options.ApiKey ??= "test-key";
        options.MaxRetries = 0;
        return new AnthropicLlmClient(Options.Create(options), new StubFactory(handler));
    }

    private static string Message(string text, string stopReason, string model = "claude-opus-5-5", string? stopDetails = null) => $$"""
        {
          "id": "msg_test",
          "type": "message",
          "role": "assistant",
          "model": "{{model}}",
          "content": [ { "type": "text", "text": {{JsonSerializer.Serialize(text)}} } ],
          "stop_reason": "{{stopReason}}",
          "stop_sequence": null,
          {{(stopDetails is null ? "" : $"\"stop_details\": {stopDetails},")}}
          "usage": { "input_tokens": 12, "output_tokens": 34 }
        }
        """;

    [Fact]
    public async Task Sends_model_prompt_effort_and_fallbacks_and_parses_the_answer()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Message("Paris.", "end_turn"));

        var response = await Client(handler).CompleteAsync(new LlmRequest("claude-opus-5-5", "Capital of France?"), CancellationToken.None);

        Assert.Equal("Paris.", response.Text);
        Assert.Equal(12, response.InputTokens);
        Assert.Equal(34, response.OutputTokens);
        Assert.Equal(LlmOutcome.Completed, response.Outcome);
        Assert.Null(response.ServedByModel);

        var (request, body) = Assert.Single(handler.Calls);
        Assert.EndsWith("/v1/messages", request.RequestUri!.AbsolutePath);
        Assert.Contains("server-side-fallback-2026-07-01", string.Join(",", request.Headers.GetValues("anthropic-beta")));
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("claude-opus-5-5", root.GetProperty("model").GetString());
        Assert.Equal("high", root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("default", root.GetProperty("fallbacks").GetString());
        Assert.Equal("Capital of France?", root.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Omits_fallbacks_when_disabled()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Message("ok", "end_turn"));

        await Client(handler, new AnthropicOptions { UseRefusalFallbacks = false, Effort = "low" })
            .CompleteAsync(new LlmRequest("claude-opus-5-5", "hi"), CancellationToken.None);

        using var json = JsonDocument.Parse(handler.Calls[0].Body);
        Assert.False(json.RootElement.TryGetProperty("fallbacks", out _));
        Assert.Equal("low", json.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task Maps_max_tokens_to_truncated()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Message("partial", "max_tokens"));

        var response = await Client(handler).CompleteAsync(new LlmRequest("claude-opus-5-5", "long"), CancellationToken.None);

        Assert.Equal(LlmOutcome.Truncated, response.Outcome);
        Assert.Equal("max_tokens", response.StopReason);
    }

    [Fact]
    public async Task Maps_refusal_with_details()
    {
        var details = """{ "type": "refusal", "category": "cyber", "explanation": "Not allowed." }""";
        var handler = new StubHandler(HttpStatusCode.OK, Message("", "refusal", stopDetails: details));

        var response = await Client(handler).CompleteAsync(new LlmRequest("claude-opus-5-5", "x"), CancellationToken.None);

        Assert.Equal(LlmOutcome.Refused, response.Outcome);
        Assert.Contains("Not allowed.", response.Note);
    }

    [Fact]
    public async Task Reports_the_model_that_actually_answered()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Message("ok", "end_turn", model: "claude-opus-4-8"));

        var response = await Client(handler).CompleteAsync(new LlmRequest("claude-opus-5-5", "x"), CancellationToken.None);

        Assert.Equal("claude-opus-4-8", response.ServedByModel);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "rejected the API key")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limit")]
    [InlineData(HttpStatusCode.InternalServerError, "server error")]
    [InlineData(HttpStatusCode.BadRequest, "rejected the request")]
    public async Task Turns_api_errors_into_readable_LlmExceptions(HttpStatusCode status, string expected)
    {
        var error = """{ "type": "error", "error": { "type": "some_error", "message": "boom" } }""";
        var handler = new StubHandler(status, error);

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            Client(handler).CompleteAsync(new LlmRequest("claude-opus-5-5", "x"), CancellationToken.None));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task Is_not_configured_without_a_key()
    {
        var previous = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        try
        {
            var client = new AnthropicLlmClient(Options.Create(new AnthropicOptions()), new StubFactory(new StubHandler(HttpStatusCode.OK, "")));

            Assert.False(client.IsConfigured);
            await Assert.ThrowsAsync<LlmException>(() => client.CompleteAsync(new LlmRequest("m", "p"), CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", previous);
        }
    }
}
