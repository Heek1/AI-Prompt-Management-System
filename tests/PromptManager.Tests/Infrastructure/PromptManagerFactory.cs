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
    private readonly TestDatabase _database = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:DefaultConnection", _database.ConnectionString);
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
