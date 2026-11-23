using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PromptManager.Data;
using PromptManager.Infrastructure;
using PromptManager.Models;

namespace PromptManager.Api;

/// <summary>What an application gets for a prompt version.</summary>
public sealed record PromptVersionResponse(
    string Slug,
    string Title,
    int Version,
    string Status,
    string Text,
    IReadOnlyList<string> Variables,
    DateTime UpdatedAt);

public sealed record RenderRequest(int? Version, string? Label, Dictionary<string, string?>? Variables);

public sealed record RenderResponse(string Slug, int Version, string Text);

/// <summary>
/// Read-only HTTP API so applications fetch a prompt from here instead of
/// hard-coding it. Authenticated with an API key (X-Api-Key header or
/// "Authorization: Bearer"); rate-limited per key.
/// </summary>
public static class PromptApi
{
    public const string RateLimitPolicy = "api";
    public const int RequestsPerMinute = 60;

    public static IServiceCollection AddPromptApi(this IServiceCollection services)
    {
        services.AddScoped<IApiKeyService, ApiKeyService>();
        services.AddOpenApi();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // Keyed by the presented key (or the client address without one),
            // so one application cannot starve another.
            options.AddPolicy(RateLimitPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    PresentedKey(context) is { } key ? "key:" + ApiKeyService.Hash(key) : "ip:" + context.Connection.RemoteIpAddress,
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = RequestsPerMinute, Window = TimeSpan.FromMinutes(1) }));
        });
        return services;
    }

    public static void MapPromptApi(this WebApplication app)
    {
        app.MapOpenApi(); // /openapi/v1.json

        var api = app.MapGroup("/api/prompts")
            .WithTags("Prompts")
            .RequireRateLimiting(RateLimitPolicy)
            .AddEndpointFilter(RequireApiKey);

        api.MapGet("/{slug}", GetAsync)
            .WithName("GetPrompt")
            .WithSummary("Get a prompt version")
            .WithDescription("The production version by default; ?label=latest for the newest, ?version=N for a specific one. Supports If-None-Match.");

        api.MapPost("/{slug}/render", RenderAsync)
            .WithName("RenderPrompt")
            .WithSummary("Fill in a prompt's variables")
            .WithDescription("Returns the text with {{variables}} replaced. No model is called.");
    }

    private static async Task<Results<Ok<PromptVersionResponse>, StatusCodeHttpResult, ProblemHttpResult>> GetAsync(
        string slug, string? label, int? version, HttpContext http, AppDbContext db)
    {
        var (prompt, chosen, error) = await ResolveAsync(db, slug, label, version);
        if (error is not null) return TypedResults.Problem(error);

        // The text of a version never changes, so its id and number identify it.
        var etag = $"\"{prompt!.Id}-{chosen!.Id}-{chosen.VersionNumber}\"";
        http.Response.Headers.ETag = etag;
        // Clients may keep the answer but must ask again before using it, so a
        // promotion is picked up on the next request (usually a cheap 304).
        http.Response.Headers.CacheControl = "private, no-cache";
        if (http.Request.Headers.IfNoneMatch.Contains(etag))
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);

        return TypedResults.Ok(new PromptVersionResponse(
            prompt.Slug, prompt.Title, chosen.VersionNumber, chosen.Status.ToString().ToLowerInvariant(),
            chosen.PromptText, PromptTemplate.ExtractVariables(chosen.PromptText), chosen.CreatedAt));
    }

    private static async Task<Results<Ok<RenderResponse>, ProblemHttpResult>> RenderAsync(
        string slug, RenderRequest request, AppDbContext db)
    {
        var (prompt, chosen, error) = await ResolveAsync(db, slug, request.Label, request.Version);
        if (error is not null) return TypedResults.Problem(error);

        var values = (request.Variables ?? []).ToDictionary(kv => kv.Key, kv => kv.Value ?? string.Empty);
        if (values.Values.Any(v => v.Length > PromptTemplate.MaxValueLength))
            return TypedResults.Problem(Problem(400, $"Values are limited to {PromptTemplate.MaxValueLength} characters."));

        var (text, missing) = PromptTemplate.Render(chosen!.PromptText, values);
        if (missing.Count > 0)
            return TypedResults.Problem(Problem(400, $"Missing values for: {string.Join(", ", missing)}."));

        return TypedResults.Ok(new RenderResponse(prompt!.Slug, chosen.VersionNumber, text));
    }

    private static async Task<(Prompt? Prompt, PromptVersion? Version, ProblemDetails? Error)> ResolveAsync(
        AppDbContext db, string slug, string? label, int? version)
    {
        label = string.IsNullOrWhiteSpace(label) ? "production" : label.Trim().ToLowerInvariant();
        if (version is null && label is not ("production" or "latest"))
            return (null, null, Problem(400, "label must be 'production' or 'latest'."));

        var prompt = await db.Prompts.AsNoTracking().FirstOrDefaultAsync(p => p.Slug == slug && !p.IsArchived);
        if (prompt is null) return (null, null, Problem(404, $"No prompt '{slug}'."));

        var number = version ?? (label == "latest" ? prompt.CurrentVersion : prompt.ProductionVersion);
        if (number is null) return (null, null, Problem(404, $"'{slug}' has no production version yet."));

        var chosen = await db.PromptVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.PromptId == prompt.Id && v.VersionNumber == number);
        return chosen is null
            ? (null, null, Problem(404, $"'{slug}' has no version {number}."))
            : (prompt, chosen, null);
    }

    private static async ValueTask<object?> RequireApiKey(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var presented = PresentedKey(http);
        var keys = http.RequestServices.GetRequiredService<IApiKeyService>();
        if (presented is null || await keys.ValidateAsync(presented) is null)
        {
            http.Response.Headers.WWWAuthenticate = "Bearer";
            return TypedResults.Problem(statusCode: 401, title: "A valid API key is required (X-Api-Key header or Authorization: Bearer).");
        }
        return await next(context);
    }

    private static string? PresentedKey(HttpContext http)
    {
        if (http.Request.Headers.TryGetValue("X-Api-Key", out var header) && !string.IsNullOrWhiteSpace(header))
            return header.ToString().Trim();
        var auth = http.Request.Headers.Authorization.ToString();
        return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null;
    }

    private static ProblemDetails Problem(int status, string detail) => new() { Status = status, Title = detail };
}
