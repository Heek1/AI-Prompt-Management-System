using System.Diagnostics;

namespace AI_Prompt_Management_System.Middleware;

public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var sw = Stopwatch.StartNew();

        await _next(context);

        sw.Stop();

        _logger.LogInformation(
            "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs}ms",
            context.Request.Method,
            context.Request.Path,
            context.Response.StatusCode,
            sw.ElapsedMilliseconds
        );
    }
}

public class AiUsageMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AiUsageMiddleware> _logger;

    public AiUsageMiddleware(RequestDelegate next, ILogger<AiUsageMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/Prompts/UsePrompt"))
        {
            _logger.LogInformation(
                "[AI USAGE] AI prompt used at {Time} by {IP}",
                DateTime.UtcNow,
                context.Connection.RemoteIpAddress
            );
        }

        await _next(context);
    }
}

public static class MiddlewareExtensions
{
    public static IApplicationBuilder UseRequestLogging(this IApplicationBuilder app)
        => app.UseMiddleware<RequestLoggingMiddleware>();

    public static IApplicationBuilder UseAiUsageLogging(this IApplicationBuilder app)
        => app.UseMiddleware<AiUsageMiddleware>();
}
