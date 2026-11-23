namespace PromptManager.Middleware;

/// <summary>
/// Response headers that limit what a browser lets the pages do.
/// Scripts may only come from this site (all page scripts live in
/// wwwroot/js/site.js); inline styles are still allowed because the views use
/// style attributes for small layout details.
/// </summary>
public static class SecurityHeaders
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "object-src 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["Content-Security-Policy"] = ContentSecurityPolicy;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
                return Task.CompletedTask;
            });
            await next();
        });
}
