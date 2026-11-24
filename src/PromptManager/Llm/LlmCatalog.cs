using Microsoft.Extensions.Options;

namespace PromptManager.Llm;

public sealed record AvailableModel(LlmModelOptions Model, bool IsAvailable, string? UnavailableReason);

/// <summary>
/// The models listed in configuration, joined with the client that serves
/// each one. A model whose client is missing or has no key is listed but
/// marked unavailable, so the UI can say why instead of hiding it.
/// </summary>
public sealed class LlmCatalog
{
    private readonly LlmOptions _options;
    private readonly Dictionary<string, ILlmClient> _clients;

    public LlmCatalog(IOptions<LlmOptions> options, IEnumerable<ILlmClient> clients)
    {
        _options = options.Value;
        _clients = clients.ToDictionary(c => c.Provider, c => (ILlmClient)new InstrumentedLlmClient(c, Find),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The model to preselect: the configured default when it can be used,
    /// otherwise the first usable one. Preselecting a disabled option would
    /// submit no model at all.
    /// </summary>
    public string DefaultModel
    {
        get
        {
            var models = List();
            return models.FirstOrDefault(m => m.IsAvailable && m.Model.Id == _options.DefaultModel)?.Model.Id
                ?? models.FirstOrDefault(m => m.IsAvailable)?.Model.Id
                ?? _options.DefaultModel;
        }
    }

    public TimeSpan Timeout => TimeSpan.FromSeconds(_options.TimeoutSeconds);

    public IReadOnlyList<AvailableModel> List() =>
        _options.Models.Select(m => Describe(m)).ToList();

    public LlmModelOptions? Find(string modelId) =>
        _options.Models.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.Ordinal));

    /// <summary>Returns the client for a configured, usable model, or null with a reason.</summary>
    public (LlmModelOptions? Model, ILlmClient? Client, string? Error) Resolve(string modelId)
    {
        var model = Find(modelId);
        if (model is null)
            return (null, null, $"Unknown model '{modelId}'.");

        var described = Describe(model);
        return described.IsAvailable
            ? (model, _clients[model.Provider], null)
            : (model, null, described.UnavailableReason);
    }

    private AvailableModel Describe(LlmModelOptions model)
    {
        if (!_clients.TryGetValue(model.Provider, out var client))
            return new(model, false, $"No client for provider '{model.Provider}'.");
        if (!client.IsConfigured)
            return new(model, false, $"{model.Provider} is not configured (missing API key).");
        return new(model, true, null);
    }
}
