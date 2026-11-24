using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PromptManager.Api;
using PromptManager.Data;
using PromptManager.Evals;
using PromptManager.Llm;
using Microsoft.AspNetCore.HttpLogging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PromptManager.Middleware;
using PromptManager.Models;
using PromptManager.Services;

var builder = WebApplication.CreateBuilder(args);

// ---------- Observability (D7) ----------
// JSON logs outside development, so a log service can index the fields
// (method, path, status, duration, trace id) instead of parsing sentences.
if (!builder.Environment.IsDevelopment())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(o =>
    {
        o.IncludeScopes = true;
        o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        o.UseUtcTimestamp = true;
    });
}

// One line per request with what is safe to keep: no query string (it holds
// searches), no headers (cookies, API keys) and no client IP.
builder.Services.AddHttpLogging(o =>
{
    o.LoggingFields = HttpLoggingFields.RequestMethod | HttpLoggingFields.RequestPath
                      | HttpLoggingFields.ResponseStatusCode | HttpLoggingFields.Duration;
    o.CombineLogs = true;
});

// Traces and metrics are always collected; they leave the process only when
// an OTLP endpoint is configured (OTEL_EXPORTER_OTLP_ENDPOINT, e.g. a
// collector, Grafana Cloud or Honeycomb). Without one this costs nearly nothing.
var otlp = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("promptmanager"))
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health")
                                                          && !ctx.Request.Path.StartsWithSegments("/lib"))
         .AddHttpClientInstrumentation()
         .AddSource("Npgsql")
         .AddSource(Telemetry.Name);
        if (otlp) t.AddOtlpExporter();
    })
    .WithMetrics(m =>
    {
        m.AddAspNetCoreInstrumentation()
         .AddHttpClientInstrumentation()
         .AddMeter(Telemetry.Name);
        if (otlp) m.AddOtlpExporter();
    });

// Services
builder.Services.AddControllersWithViews();
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException(
        "ConnectionStrings:DefaultConnection is not set. For local development run `docker compose up db`; " +
        "elsewhere set the ConnectionStrings__DefaultConnection environment variable.");

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(ConnectionStrings.ToNpgsql(connectionString)));

builder.Services
    .AddIdentity<AppUser, IdentityRole>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 10;
        options.Password.RequireNonAlphanumeric = false;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

// Form-token and TempData cookies get the Secure flag on https requests too
// (the sign-in cookie already does by default).
builder.Services.AddAntiforgery(options => options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest);
builder.Services.Configure<Microsoft.AspNetCore.Mvc.CookieTempDataProviderOptions>(options => options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest);

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.SlidingExpiration = true;
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
});

// Re-check the sign-in cookie against the database every minute (default 30),
// so a deleted or locked-out account is signed out quickly.
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();

builder.Services.AddMemoryCache();
builder.Services.AddScoped<ILookupService, LookupService>();
builder.Services.AddScoped<ICollaborationService, CollaborationService>();
builder.Services.AddScoped<IPromptService, PromptService>();
builder.Services.AddScoped<PromptManager.Services.Transfer.PromptTransferService>();
builder.Services.AddScoped<IPromptRunner, PromptRunner>();
builder.Services.AddScoped<IInputSetService, InputSetService>();
builder.Services.AddScoped<IVersionLifecycle, VersionLifecycle>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ICostService, CostService>();
builder.Services.AddPromptApi();

// Liveness says the process answers; readiness also needs the database.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);
builder.Services.AddSingleton<EvalQueue>();
builder.Services.AddScoped<EvalJudge>();
builder.Services.AddScoped<ImprovementAssistant>();
builder.Services.AddScoped<EvalExecutor>();
builder.Services.AddScoped<IEvalService, EvalService>();
builder.Services.AddHostedService<EvalWorker>();
builder.Services.AddLlmClients(builder.Configuration);

// Antiforgery tokens and sign-in cookies are encrypted with data-protection
// keys. They are kept in the database, so they survive restarts and new
// deployments on hosts without a persistent disk (such as Render's free plan);
// otherwise every deploy would sign everyone out and break open forms.
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<AppDbContext>()
    .SetApplicationName("PromptManager");

var app = builder.Build();

// Auto-migrate on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();

    var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    if (!await roles.RoleExistsAsync(Roles.Admin))
        await roles.CreateAsync(new IdentityRole(Roles.Admin));

    if (app.Environment.IsDevelopment())
        await DevDataSeeder.SeedAsync(db);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseStatusCodePagesWithReExecute("/Home/Error/{0}");
    // Only takes effect on https requests (behind Render's proxy: forwarded headers).
    app.UseHsts();
}

app.UseSecurityHeaders();

// One fixed culture, so dates and numbers look the same on every server.
var culture = new System.Globalization.CultureInfo("en-GB");
app.UseRequestLocalization(new Microsoft.AspNetCore.Builder.RequestLocalizationOptions
{
    DefaultRequestCulture = new Microsoft.AspNetCore.Localization.RequestCulture(culture),
    SupportedCultures = [culture],
    SupportedUICultures = [culture]
});

app.UseStaticFiles();
// After static files, so the log has page and API requests, not every stylesheet.
app.UseHttpLogging();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapPromptApi();

// Status only (Healthy/Unhealthy as text); no environment name or counts.
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") });
// Kept for Render's health check setting.
app.MapHealthChecks("/health", new() { Predicate = c => c.Tags.Contains("ready") });

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Prompts}/{action=Index}/{id?}");

app.Run();
