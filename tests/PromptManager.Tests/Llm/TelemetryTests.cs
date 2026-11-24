using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using PromptManager.Llm;

namespace PromptManager.Tests.Llm;

/// <summary>D7: model calls are traced and counted through the catalog, without any text.</summary>
public class TelemetryTests
{
    private sealed class Fixed : ILlmClient
    {
        public string Provider => "Fixed";
        public bool IsConfigured => true;
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct) =>
            Task.FromResult(new LlmResponse("secret answer", 1_000, 500, LlmOutcome.Completed, "end_turn"));
    }

    [Fact]
    public async Task Calls_through_the_catalog_record_a_span_tokens_and_cost()
    {
        var options = Options.Create(new LlmOptions
        {
            Models = [new LlmModelOptions { Id = "fixed-1", DisplayName = "Fixed", Provider = "Fixed", InputPricePerMillion = 2, OutputPricePerMillion = 10 }]
        });
        var catalog = new LlmCatalog(options, [new Fixed()]);

        var measurements = new List<(string Name, double Value, string? Model)>();
        using var meters = new MeterListener();
        meters.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == Telemetry.Name) listener.EnableMeasurementEvents(instrument);
        };
        void Record(Instrument i, double v, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            string? model = null;
            foreach (var t in tags) if (t.Key == "model") model = t.Value as string;
            lock (measurements) measurements.Add((i.Name, v, model));
        }
        meters.SetMeasurementEventCallback<long>((i, v, t, _) => Record(i, v, t));
        meters.SetMeasurementEventCallback<double>((i, v, t, _) => Record(i, v, t));
        meters.Start();

        var spans = new List<Activity>();
        using var activities = new ActivityListener
        {
            ShouldListenTo = s => s.Name == Telemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (spans) spans.Add(a); }
        };
        ActivitySource.AddActivityListener(activities);

        var (_, client, _) = catalog.Resolve("fixed-1");
        await client!.CompleteAsync(new LlmRequest("fixed-1", "secret prompt"), CancellationToken.None);

        var span = Assert.Single(spans, s => s.GetTagItem("gen_ai.request.model") as string == "fixed-1");
        Assert.Equal(1000L, span.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.DoesNotContain(span.TagObjects, t => t.Value is string v && v.Contains("secret"));

        lock (measurements)
        {
            Assert.Contains(measurements, m => m is { Name: "promptmanager.llm.calls", Value: 1, Model: "fixed-1" });
            Assert.Equal(1500, measurements.Where(m => m.Name == "promptmanager.llm.tokens" && m.Model == "fixed-1").Sum(m => m.Value));
            // 1000 * $2/M + 500 * $10/M = $0.007
            Assert.Contains(measurements, m => m.Name == "promptmanager.llm.cost" && m.Model == "fixed-1" && Math.Abs(m.Value - 0.007) < 1e-9);
        }
    }
}
