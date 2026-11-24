using System.Net;
using PromptManager.Infrastructure;
using PromptManager.Tests.Infrastructure;

namespace PromptManager.Tests.Integration;

/// <summary>C8: the interface in Ukrainian, chosen by the browser or the switch; English stays the default.</summary>
public sealed class LocalizationTests : IDisposable
{
    private readonly PromptManagerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task English_is_the_default()
    {
        var html = await _factory.CreateClient().GetStringAsync("/");

        Assert.Contains("<html lang=\"en\">", html);
        Assert.Contains("<h1>Library</h1>", html);
    }

    [Fact]
    public async Task Browser_language_picks_Ukrainian()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("uk-UA,uk;q=0.9,en;q=0.5");

        var html = await client.GetStringAsync("/");

        Assert.Contains("<html lang=\"uk\">", html);
        Assert.Contains("<h1>Бібліотека</h1>", html);
        Assert.Contains("0 промптів.", html);
        Assert.Contains("<title>Бібліотека | Prompt Manager</title>", html);
    }

    [Fact]
    public async Task The_switch_remembers_the_language_and_returns_to_the_page()
    {
        var client = _factory.CreateNoRedirectClient();

        var response = await client.PostFormAsync("/Runs", "/Language", new Dictionary<string, string>
        {
            ["ui"] = "uk", ["returnUrl"] = "/Runs"
        });
        Assert.Equal("/Runs", response.Headers.Location!.OriginalString);
        Assert.Contains("<h1>Запуски</h1>", await client.GetStringAsync("/Runs"));

        // Back to English, and an address outside the app is never followed.
        var back = await client.PostFormAsync("/", "/Language", new Dictionary<string, string>
        {
            ["ui"] = "en", ["returnUrl"] = "https://example.com/"
        });
        Assert.Equal("/", back.Headers.Location!.OriginalString);
        Assert.Contains("<h1>Library</h1>", await client.GetStringAsync("/"));
    }

    [Fact]
    public async Task Validation_and_counts_are_translated()
    {
        var client = _factory.CreateNoRedirectClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("uk");
        await client.SignUpAsync("owner@test.local", "Olena");

        var form = await client.PostFormAsync("/Prompts/Create", "/Prompts/Create", new Dictionary<string, string>
        {
            ["Title"] = "", ["PromptText"] = "Summarise the following text in three bullet points.", ["Category"] = "Learning", ["AiTool"] = "Claude"
        });
        Assert.Equal(HttpStatusCode.OK, form.StatusCode);
        Assert.Contains("Вкажіть назву.", await form.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(1, "1 промпт")]
    [InlineData(3, "3 промпти")]
    [InlineData(5, "5 промптів")]
    [InlineData(11, "11 промптів")]
    [InlineData(21, "21 промпт")]
    [InlineData(22, "22 промпти")]
    [InlineData(112, "112 промптів")]
    public void Ukrainian_plurals_follow_the_number(int n, string expected)
    {
        var forms = "{0} промпт|{0} промпти|{0} промптів".Split('|');

        Assert.Equal(expected, string.Format(forms[LocalizerExtensions.UkrainianForm(n)], n));
    }
}
