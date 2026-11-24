using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PromptManager.Llm;

/// <summary>Names of the app's own traces and metrics (D7).</summary>
public static class Telemetry
{
    public const string Name = "PromptManager";

    public static readonly ActivitySource Source = new(Name);
    public static readonly Meter Meter = new(Name);

    public static readonly Counter<long> LlmCalls = Meter.CreateCounter<long>("promptmanager.llm.calls", "{call}", "Model calls, by model and outcome.");
    public static readonly Counter<long> LlmTokens = Meter.CreateCounter<long>("promptmanager.llm.tokens", "{token}", "Tokens sent and received, by model and direction.");
    public static readonly Counter<double> LlmCost = Meter.CreateCounter<double>("promptmanager.llm.cost", "USD", "Cost of model calls at the configured prices.");
    public static readonly Histogram<double> LlmDuration = Meter.CreateHistogram<double>("promptmanager.llm.duration", "s", "Time a model call took.");
}

/// <summary>
/// Wraps every provider client (see LlmCatalog), so runs, eval answers, judge
/// calls and suggestions are all traced and counted in one place. Only the
/// model, outcome and token counts are recorded, never prompt or answer text.
/// </summary>
public sealed class InstrumentedLlmClient(ILlmClient inner, Func<string, LlmModelOptions?> findModel) : ILlmClient
{
    public string Provider => inner.Provider;
    public bool IsConfigured => inner.IsConfigured;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        using var activity = Telemetry.Source.StartActivity("llm.complete", ActivityKind.Client);
        activity?.SetTag("gen_ai.system", inner.Provider.ToLowerInvariant());
        activity?.SetTag("gen_ai.request.model", request.Model);
        activity?.SetTag("gen_ai.request.max_tokens", request.MaxTokens);

        var model = new KeyValuePair<string, object?>("model", request.Model);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await inner.CompleteAsync(request, ct);
            var outcome = response.Outcome.ToString().ToLowerInvariant();

            activity?.SetTag("gen_ai.usage.input_tokens", response.InputTokens);
            activity?.SetTag("gen_ai.usage.output_tokens", response.OutputTokens);
            activity?.SetTag("gen_ai.response.finish_reason", response.StopReason);
            if (response.ServedByModel is { } served) activity?.SetTag("gen_ai.response.model", served);

            Telemetry.LlmCalls.Add(1, model, new("outcome", outcome));
            Telemetry.LlmTokens.Add(response.InputTokens, model, new("direction", "input"));
            Telemetry.LlmTokens.Add(response.OutputTokens, model, new("direction", "output"));
            if (findModel(response.ServedByModel ?? request.Model) is { } priced)
                Telemetry.LlmCost.Add((double)priced.CostOf(response.InputTokens, response.OutputTokens), model);
            return response;
        }
        catch (Exception ex) when (ex is LlmException or OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            Telemetry.LlmCalls.Add(1, model, new("outcome", ex is LlmException ? "failed" : "cancelled"));
            throw;
        }
        finally
        {
            Telemetry.LlmDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, model);
        }
    }
}
