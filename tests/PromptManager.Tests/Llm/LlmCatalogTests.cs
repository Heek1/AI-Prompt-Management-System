using Microsoft.Extensions.Options;
using PromptManager.Llm;

namespace PromptManager.Tests.Llm;

public class LlmCatalogTests
{
    private sealed class FakeClient(string provider, bool configured) : ILlmClient
    {
        public string Provider => provider;
        public bool IsConfigured => configured;
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    private static LlmCatalog Catalog(params ILlmClient[] clients) => new(Options.Create(new LlmOptions
    {
        Models =
        [
            new() { Id = "a-model", Provider = "A", InputPricePerMillion = 4m, OutputPricePerMillion = 20m },
            new() { Id = "b-model", Provider = "B" },
            new() { Id = "c-model", Provider = "Missing" }
        ]
    }), clients);

    [Fact]
    public void Resolves_a_configured_model()
    {
        var (model, client, error) = Catalog(new FakeClient("A", true)).Resolve("a-model");

        Assert.NotNull(model);
        Assert.Equal("A", client!.Provider);
        Assert.Null(error);
    }

    [Fact]
    public void Explains_why_a_model_is_unavailable()
    {
        var catalog = Catalog(new FakeClient("A", true), new FakeClient("B", false));

        Assert.Contains("missing API key", catalog.Resolve("b-model").Error);
        Assert.Contains("No client", catalog.Resolve("c-model").Error);
        Assert.Contains("Unknown model", catalog.Resolve("nope").Error);
        Assert.Equal(new[] { true, false, false }, catalog.List().Select(m => m.IsAvailable));
    }

    [Fact]
    public void Cost_uses_per_million_prices()
    {
        var model = new LlmModelOptions { InputPricePerMillion = 4m, OutputPricePerMillion = 20m };

        // 1,000 in at $4/M + 500 out at $20/M = 0.004 + 0.01
        Assert.Equal(0.014m, model.CostOf(1_000, 500));
    }

    [Fact]
    public async Task Echo_returns_the_prompt_without_calling_a_model()
    {
        var response = await new EchoLlmClient().CompleteAsync(new LlmRequest("echo", "say hello world"), CancellationToken.None);

        Assert.Contains("say hello world", response.Text);
        Assert.Equal(3, response.InputTokens);
        Assert.Equal(LlmOutcome.Completed, response.Outcome);
    }

    [Fact]
    public void Default_model_falls_back_to_the_first_usable_one()
    {
        var options = new LlmOptions
        {
            DefaultModel = "b-model",
            Models = [new() { Id = "b-model", Provider = "B" }, new() { Id = "a-model", Provider = "A" }]
        };

        Assert.Equal("a-model", new LlmCatalog(Options.Create(options), [new FakeClient("A", true), new FakeClient("B", false)]).DefaultModel);
        Assert.Equal("b-model", new LlmCatalog(Options.Create(options), [new FakeClient("A", true), new FakeClient("B", true)]).DefaultModel);
    }
}
