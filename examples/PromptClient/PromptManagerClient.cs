using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace PromptClient;

public sealed record PromptVersion(string Slug, string Title, int Version, string Status, string Text, List<string> Variables, DateTime UpdatedAt);

public sealed record RenderedPrompt(string Slug, int Version, string Text);

/// <summary>
/// A minimal client for the Prompt Manager API. It keeps the last answer per
/// prompt and revalidates it with If-None-Match, so asking again costs a 304
/// until someone promotes a new version.
/// </summary>
public sealed class PromptManagerClient
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, (EntityTagHeaderValue ETag, PromptVersion Value)> _cache = [];

    /// <param name="http">BaseAddress must point at the Prompt Manager site.</param>
    public PromptManagerClient(HttpClient http, string apiKey)
    {
        _http = http;
        _http.DefaultRequestHeaders.Remove("X-Api-Key");
        _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
    }

    /// <summary>How many requests were answered with 304 (for the demo and tests).</summary>
    public int NotModifiedCount { get; private set; }

    /// <summary>The production version of a prompt.</summary>
    public async Task<PromptVersion> GetAsync(string slug, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/prompts/{Uri.EscapeDataString(slug)}");
        if (_cache.TryGetValue(slug, out var cached))
            request.Headers.IfNoneMatch.Add(cached.ETag);

        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            NotModifiedCount++;
            return cached.Value;
        }

        await EnsureSuccessAsync(response, ct);
        var value = (await response.Content.ReadFromJsonAsync<PromptVersion>(ct))!;
        if (response.Headers.ETag is { } etag)
            _cache[slug] = (etag, value);
        return value;
    }

    /// <summary>The production version with its variables filled in. No model is called.</summary>
    public async Task<RenderedPrompt> RenderAsync(string slug, IDictionary<string, string> variables, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync($"api/prompts/{Uri.EscapeDataString(slug)}/render", new { variables }, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<RenderedPrompt>(ct))!;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {body}", null, response.StatusCode);
    }
}
