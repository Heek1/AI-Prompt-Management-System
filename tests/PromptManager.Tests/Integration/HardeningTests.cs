using System.Net;
using System.Text.RegularExpressions;
using PromptManager.Tests.Infrastructure;

namespace PromptManager.Tests.Integration;

public sealed class HardeningTests : IDisposable
{
    private readonly PromptManagerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Pages_send_security_headers()
    {
        var response = await _factory.CreateClient().GetAsync("/Prompts");

        var csp = string.Join("", response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("script-src 'self';", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("strict-origin-when-cross-origin", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task Pages_load_no_external_or_inline_scripts()
    {
        var client = _factory.CreateNoRedirectClient();
        await client.SignUpAsync("owner@test.local");
        var create = await client.PostFormAsync("/Prompts/Create", "/Prompts/Create", new Dictionary<string, string>
        {
            ["Title"] = "T", ["PromptText"] = "Summarise the text in three bullet points.", ["Category"] = "Learning", ["AiTool"] = "Claude"
        });
        var details = await client.GetStringAsync(create.Headers.Location!.OriginalString);

        foreach (var html in new[] { await client.GetStringAsync("/Prompts"), details, await client.GetStringAsync("/Prompts/Create") })
        {
            Assert.DoesNotContain("cdn.jsdelivr.net", html);
            Assert.DoesNotContain("onclick=", html);
            Assert.DoesNotMatch(new Regex(@"<script>"), html);
        }
        Assert.Contains("data-confirm=\"Archive this prompt?\"", details);
        Assert.Contains("data-copy-target=\"#promptText\"", details);
    }

    [Fact]
    public async Task Form_pages_load_jquery_before_validation()
    {
        var client = _factory.CreateNoRedirectClient();
        await client.SignUpAsync("owner@test.local");

        var html = await client.GetStringAsync("/Prompts/Create");

        var jquery = html.IndexOf("/lib/jquery/dist/jquery.min.js", StringComparison.Ordinal);
        var validate = html.IndexOf("/lib/jquery-validation/dist/jquery.validate.min.js", StringComparison.Ordinal);
        Assert.True(jquery > 0 && validate > jquery, "jQuery must load before jquery-validation");
    }

    [Theory]
    [InlineData("/js/site.js")]
    [InlineData("/lib/bootstrap/dist/css/bootstrap.min.css")]
    [InlineData("/lib/bootstrap-icons/font/bootstrap-icons.min.css")]
    [InlineData("/lib/bootstrap-icons/font/fonts/bootstrap-icons.woff2")]
    [InlineData("/lib/jquery/dist/jquery.min.js")]
    public async Task Local_assets_are_served(string path)
    {
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateClient().GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Dates_use_a_fixed_culture()
    {
        var client = _factory.CreateNoRedirectClient();
        await client.SignUpAsync("owner@test.local");
        var create = await client.PostFormAsync("/Prompts/Create", "/Prompts/Create", new Dictionary<string, string>
        {
            ["Title"] = "T", ["PromptText"] = "Summarise the text in three bullet points.", ["Category"] = "Learning", ["AiTool"] = "Claude"
        });

        var html = await client.GetStringAsync(create.Headers.Location!.OriginalString);

        var month = DateTime.UtcNow.ToString("MMM", new System.Globalization.CultureInfo("en-GB"));
        Assert.Matches(new Regex($@"Created: \d\d {month} \d{{4}}"), html);
    }
}
