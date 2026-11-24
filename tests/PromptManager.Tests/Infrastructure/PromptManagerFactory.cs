using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PromptManager.Data;

namespace PromptManager.Tests.Infrastructure;

/// <summary>
/// Runs the real app in memory against its own PostgreSQL test database.
/// The "Testing" environment is not Development, so the sample-data seeder is
/// off and the production error pages are on, as in a deployed app.
/// </summary>
public sealed class PromptManagerFactory : WebApplicationFactory<Program>
{
    public const string AdminEmail = "admin@test.local";
    public const string Password = "Test-password-1";

    private readonly TestDatabase _database = new();

    // Each test server would otherwise watch its config and static files with
    // inotify; with dozens of servers in one run, Linux CI hits its limit of
    // 128 watcher instances and requests fail. Tests never change files.
    static PromptManagerFactory()
    {
        Environment.SetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange", "false");
        Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "true");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:DefaultConnection", _database.ConnectionString);
        builder.UseSetting("Auth:AdminEmails:0", AdminEmail);
        // A second free model, so model comparison can be tested offline.
        builder.UseSetting("Llm:Models:4:Id", "echo-2");
        builder.UseSetting("Llm:Models:4:DisplayName", "Echo 2");
        builder.UseSetting("Llm:Models:4:Provider", "Echo");
    }

    public AppDbContext CreateDbContext() => _database.CreateContext();

    public HttpClient CreateNoRedirectClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _database.Dispose();
    }
}

public static class FormPosting
{
    /// <summary>Registers an account through the real form; the client keeps the sign-in cookie.</summary>
    public static async Task SignUpAsync(this HttpClient client, string email, string displayName = "Tester")
    {
        var response = await client.PostFormAsync("/Account/Register", "/Account/Register", new Dictionary<string, string>
        {
            ["Email"] = email,
            ["DisplayName"] = displayName,
            ["Password"] = PromptManagerFactory.Password,
            ["ConfirmPassword"] = PromptManagerFactory.Password
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    public static async Task SignOutAsync(this HttpClient client)
    {
        var page = await client.GetStringAsync("/Prompts");
        var token = TokenPattern.Match(page).Groups[1].Value;
        var response = await client.PostAsync("/Account/Logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token)
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static readonly Regex TokenPattern =
        new("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"", RegexOptions.Compiled);

    /// <summary>
    /// GETs <paramref name="formPage"/> to obtain the antiforgery cookie and token,
    /// then POSTs <paramref name="fields"/> to <paramref name="action"/>.
    /// </summary>
    public static async Task<HttpResponseMessage> PostFormAsync(
        this HttpClient client, string formPage, string action, IDictionary<string, string> fields)
    {
        var page = await client.GetAsync(formPage);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var match = TokenPattern.Match(html);
        Assert.True(match.Success, $"No antiforgery token on {formPage}");

        var body = new Dictionary<string, string>(fields)
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value)
        };
        return await client.PostAsync(action, new FormUrlEncodedContent(body));
    }
}

internal static class ServiceCollectionExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        foreach (var d in services.Where(d => d.ServiceType == typeof(T)).ToList())
            services.Remove(d);
    }
}
