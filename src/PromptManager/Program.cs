using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PromptManager.Data;
using PromptManager.Evals;
using PromptManager.Llm;
using PromptManager.Middleware;
using PromptManager.Models;
using PromptManager.Services;

var builder = WebApplication.CreateBuilder(args);

// Services
builder.Services.AddControllersWithViews();
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException(
        "ConnectionStrings:DefaultConnection is not set. For local development run `docker compose up db`; " +
        "elsewhere set the ConnectionStrings__DefaultConnection environment variable.");

// Kerberos/GSS is never used here, and probing for it logs a libgssapi error
// in the slim runtime image, so turn it off unless the string says otherwise.
var npgsql = new NpgsqlConnectionStringBuilder(connectionString);
if (!connectionString.Contains("GSS", StringComparison.OrdinalIgnoreCase))
    npgsql.GssEncryptionMode = GssEncryptionMode.Disable;

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(npgsql.ConnectionString));

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

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();

builder.Services.AddScoped<IPromptService, PromptService>();
builder.Services.AddScoped<IPromptRunner, PromptRunner>();
builder.Services.AddScoped<IInputSetService, InputSetService>();
builder.Services.AddSingleton<EvalQueue>();
builder.Services.AddScoped<EvalJudge>();
builder.Services.AddScoped<EvalExecutor>();
builder.Services.AddScoped<IEvalService, EvalService>();
builder.Services.AddHostedService<EvalWorker>();
builder.Services.AddLlmClients(builder.Configuration);

// Antiforgery tokens are encrypted with data-protection keys. In a container
// the default key folder is lost on restart, which would break every open
// form, so keep the keys on the data volume when a path is configured.
if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
        .SetApplicationName("PromptManager");
}

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
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// Custom Middleware
app.UseRequestLogging();
app.UseAiUsageLogging();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Prompts}/{action=Index}/{id?}");

app.Run();
