using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PromptManager.Tests.Infrastructure;

namespace PromptManager.Tests.Integration;

/// <summary>Bugs found in the QA pass, each pinned by a test.</summary>
public sealed class QaRegressionTests : IDisposable
{
    private readonly PromptManagerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> SignedInAsync(string email = "owner@test.local", string? language = null)
    {
        var client = _factory.CreateNoRedirectClient();
        if (language is not null) client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(language);
        await client.SignUpAsync(email, "Owner");
        return client;
    }

    private static async Task<int> CreateAsync(HttpClient client, string title = "QA prompt")
    {
        var response = await client.PostFormAsync("/Prompts/Create", "/Prompts/Create", new Dictionary<string, string>
        {
            ["Title"] = title, ["PromptText"] = "Summarise the following text in three bullet points.",
            ["Category"] = "Learning", ["AiTool"] = "Claude"
        });
        return int.Parse(response.Headers.Location!.OriginalString.Split('/').Last());
    }

    private static Task<HttpResponseMessage> ArchiveAsync(HttpClient client, int id) =>
        client.PostFormAsync($"/Prompts/Details/{id}", "/Prompts/Archive", new Dictionary<string, string> { ["id"] = id.ToString() });

    [Fact]
    public async Task Archived_prompt_cannot_be_edited_or_given_a_version()
    {
        var client = await SignedInAsync();
        var id = await CreateAsync(client);
        await ArchiveAsync(client, id);

        Assert.Equal($"/Prompts/Details/{id}", (await client.GetAsync($"/Prompts/Edit/{id}")).Headers.Location!.OriginalString);
        Assert.Equal($"/Prompts/Details/{id}", (await client.GetAsync($"/Prompts/CreateVersion/{id}")).Headers.Location!.OriginalString);

        // A hand-made POST is refused too, and nothing changes.
        var post = await client.PostFormAsync($"/Prompts/Details/{id}", "/Prompts/Edit", new Dictionary<string, string>
        {
            ["Id"] = id.ToString(), ["Title"] = "Changed while archived", ["Category"] = "Learning", ["AiTool"] = "Claude"
        });
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        await using var db = _factory.CreateDbContext();
        Assert.Equal("QA prompt", (await db.Prompts.SingleAsync(p => p.Id == id)).Title);
    }

    [Fact]
    public async Task Import_does_not_add_a_version_to_an_archived_prompt()
    {
        var client = await SignedInAsync();
        var id = await CreateAsync(client);
        var yaml = (await client.GetStringAsync($"/Prompts/Export/{id}?format=yaml")).Replace("three bullet points.", "two bullet points, please.");
        await ArchiveAsync(client, id);

        var html = await (await UploadAsync(client, ("p.yaml", Encoding.UTF8.GetBytes(yaml)))).Content.ReadAsStringAsync();

        Assert.Contains("archived; restore it", html);
        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, (await db.Prompts.SingleAsync(p => p.Id == id)).CurrentVersion);
    }

    [Theory]
    [InlineData("arr.json", "[1, 2, 3]")]
    [InlineData("number.json", "42")]
    [InlineData("list.yaml", "- just\n- a list")]
    public async Task Import_of_a_file_with_the_wrong_shape_is_an_error_not_a_crash(string name, string content)
    {
        var client = await SignedInAsync();

        var response = await UploadAsync(client, (name, Encoding.UTF8.GetBytes(content)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains($"{name}: not a valid", html);
        Assert.DoesNotContain("PromptManager.Services", html); // no .NET type names in messages
    }

    [Fact]
    public async Task A_page_past_the_end_shows_the_last_page()
    {
        var client = await SignedInAsync();
        await CreateAsync(client, "Only prompt");

        Assert.Contains("Only prompt", await client.GetStringAsync("/?page=999"));
    }

    [Fact]
    public async Task Sign_up_errors_are_translated_including_implicit_and_account_errors()
    {
        var client = _factory.CreateNoRedirectClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("uk");

        var empty = await (await client.PostFormAsync("/Account/Register", "/Account/Register", new Dictionary<string, string>
        {
            ["Email"] = "", ["DisplayName"] = "", ["Password"] = "", ["ConfirmPassword"] = ""
        })).Content.ReadAsStringAsync();
        Assert.Contains("Заповніть поле «Повторіть пароль».", empty);
        Assert.DoesNotContain("field is required", empty);

        var bad = await (await client.PostFormAsync("/Account/Register", "/Account/Register", new Dictionary<string, string>
        {
            ["Email"] = "not-an-email", ["DisplayName"] = "A", ["Password"] = "abcdefghijk", ["ConfirmPassword"] = "abcdefghijk"
        })).Content.ReadAsStringAsync();
        Assert.Contains("Поле «E-mail» не схоже на адресу e-mail.", bad);

        var weak = await (await client.PostFormAsync("/Account/Register", "/Account/Register", new Dictionary<string, string>
        {
            ["Email"] = "weak@test.local", ["DisplayName"] = "A", ["Password"] = "abcdefghijk", ["ConfirmPassword"] = "abcdefghijk"
        })).Content.ReadAsStringAsync();
        Assert.Contains("Пароль має містити цифру.", weak);
    }

    [Fact]
    public async Task Service_messages_with_values_are_translated()
    {
        var client = await SignedInAsync(language: "uk");
        var id = await CreateAsync(client);

        var response = await client.PostFormAsync($"/Prompts/Details/{id}", "/Prompts/Comment", new Dictionary<string, string>
        {
            ["promptId"] = id.ToString(), ["versionNumber"] = "7", ["body"] = "hello"
        });

        Assert.Contains("Версії 7 не існує.", await client.GetStringAsync(response.Headers.Location!.OriginalString.Split('#')[0]));
    }

    [Fact]
    public async Task Signed_in_people_are_sent_on_from_the_sign_in_pages()
    {
        var client = await SignedInAsync();

        Assert.Equal("/", (await client.GetAsync("/Account/Login")).Headers.Location!.OriginalString);
        Assert.Equal("/Runs", (await client.GetAsync("/Account/Register?returnUrl=%2FRuns")).Headers.Location!.OriginalString);
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, params (string Name, byte[] Content)[] files)
    {
        var page = await client.GetStringAsync("/Prompts/Import");
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        using var form = new MultipartFormDataContent { { new StringContent(WebUtility.HtmlDecode(token)), "__RequestVerificationToken" } };
        foreach (var (name, content) in files)
        {
            var part = new ByteArrayContent(content);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(part, "files", name);
        }
        return await client.PostAsync("/Prompts/Import", form);
    }
}
