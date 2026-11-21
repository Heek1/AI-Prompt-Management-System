namespace PromptManager.Llm;

/// <summary>
/// Offline stand-in that returns the prompt it was given. It makes the Run
/// feature usable without an API key (local development, demos, tests), and
/// its token counts are a rough word count, not a real tokenizer.
/// </summary>
public sealed class EchoLlmClient : ILlmClient
{
    public const string ProviderName = "Echo";

    public string Provider => ProviderName;

    public bool IsConfigured => true;

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var text = "Echo (no model was called):\n\n" + request.Prompt;
        return Task.FromResult(new LlmResponse(
            Text: text,
            InputTokens: CountWords(request.Prompt),
            OutputTokens: CountWords(text),
            Outcome: LlmOutcome.Completed,
            StopReason: "end_turn"));
    }

    private static long CountWords(string s) =>
        s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
