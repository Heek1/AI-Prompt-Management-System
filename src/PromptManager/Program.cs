using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PromptManager.Data;
using PromptManager.Llm;
using PromptManager.Middleware;
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

builder.Services.AddScoped<IPromptService, PromptService>();
builder.Services.AddScoped<IPromptRunner, PromptRunner>();
builder.Services.AddScoped<IInputSetService, InputSetService>();
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
app.UseAuthorization();

// Custom Middleware
app.UseRequestLogging();
app.UseAiUsageLogging();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Prompts}/{action=Index}/{id?}");

app.Run();
