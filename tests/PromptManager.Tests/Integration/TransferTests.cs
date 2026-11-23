using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PromptManager.Models;
using PromptManager.Services.Transfer;
using PromptManager.Tests.Infrastructure;

namespace PromptManager.Tests.Integration;

/// <summary>Import and export as YAML, JSON and a zip folder (C5).</summary>
public sealed class TransferTests : IDisposable
{
    private readonly PromptManagerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> SignedInAsync(string email, string name = "Tester")
    {
        var client = _factory.CreateNoRedirectClient();
        await client.SignUpAsync(email, name);
        return client;
    }

    private const string Text1 = "Reply to {{question}} politely,\nin under 120 words.";
    private const string Text2 = "Reply to {{question}} warmly,\nin under 80 words.";

    /// <summary>A prompt with two versions (v1 in production), tags and one eval case.</summary>
    private async Task<int> SeedAsync(HttpClient client)
    {
        var create = await client.PostFormAsync("/Prompts/Create", "/Prompts/Create", new Dictionary<string, string>
        {
            ["Title"] = "Support reply", ["PromptText"] = Text1, ["Category"] = "Business Analysis",
            ["AiTool"] = "Claude", ["Tags"] = "support, email"
        });
        var id = int.Parse(create.Headers.Location!.OriginalString.Split('/').Last());
        await client.PostFormAsync($"/Prompts/CreateVersion/{id}", "/Prompts/CreateVersion", new Dictionary<string, string>
        {
            ["PromptId"] = id.ToString(), ["PromptText"] = Text2, ["ChangeNote"] = "Warmer"
        });
        await client.PostFormAsync($"/Evals/Case/{id}", "/Evals/Case", new Dictionary<string, string>
        {
            ["Id"] = "0", ["PromptId"] = id.ToString(), ["Name"] = "Mentions refunds",
            ["Variables[question]"] = "Can I get a refund?", ["MustContain"] = "refund"
        });
        return id;
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

    [Fact]
    public async Task Yaml_export_has_versions_tags_cases_and_readable_text()
    {
        var client = await SignedInAsync("owner@test.local");
        var id = await SeedAsync(client);

        var response = await client.GetAsync($"/Prompts/Export/{id}?format=yaml");
        var yaml = await response.Content.ReadAsStringAsync();

        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Contains("slug: support-reply", yaml);
        Assert.Contains("productionVersion: 1", yaml);
        // Multi-line text is a literal block, as it reads in the app.
        Assert.Contains("text: |-\n    Reply to {{question}} politely,\n    in under 120 words.", yaml.Replace("\r\n", "\n"));
        Assert.Contains("- support", yaml);
        Assert.Contains("name: Mentions refunds", yaml);
    }

    [Fact]
    public async Task Json_export_imported_by_someone_else_recreates_the_prompt()
    {
        var owner = await SignedInAsync("owner@test.local");
        await SeedAsync(owner);
        var json = await owner.GetByteArrayAsync("/Prompts/Export?format=json");

        var other = await SignedInAsync("other@test.local", "Other");
        var html = await (await UploadAsync(other, ("prompts.json", json))).Content.ReadAsStringAsync();

        Assert.Contains(">created<", html);
        Assert.Contains("slug support-reply-2 because support-reply belongs to someone else", html);
        await using var db = _factory.CreateDbContext();
        var copy = await db.Prompts.Include(p => p.Versions).Include(p => p.PromptTags).SingleAsync(p => p.Slug == "support-reply-2");
        Assert.Equal(2, copy.CurrentVersion);
        Assert.Equal(1, copy.ProductionVersion);
        Assert.Equal(Text2, copy.CurrentText);
        Assert.Equal(VersionStatus.Production, copy.Versions.Single(v => v.VersionNumber == 1).Status);
        Assert.Equal(VersionStatus.Draft, copy.Versions.Single(v => v.VersionNumber == 2).Status);
        Assert.Equal(2, copy.PromptTags.Count);
        Assert.Equal(1, await db.EvalCases.CountAsync(c => c.PromptId == copy.Id && c.Name == "Mentions refunds"));
    }

    [Fact]
    public async Task Changed_file_for_an_own_prompt_becomes_a_draft_version_and_the_same_text_changes_nothing()
    {
        var owner = await SignedInAsync("owner@test.local");
        var id = await SeedAsync(owner);
        var yaml = await owner.GetStringAsync($"/Prompts/Export/{id}?format=yaml");

        var same = await (await UploadAsync(owner, ("support-reply.yaml", Encoding.UTF8.GetBytes(yaml)))).Content.ReadAsStringAsync();
        Assert.Contains(">unchanged<", same);

        // Edited in git: CRLF line endings and a changed last version.
        var edited = yaml.Replace("in under 80 words.", "in under 60 words, and ask for the order number.").Replace("\n", "\r\n");
        var changed = await (await UploadAsync(owner, ("support-reply.yaml", Encoding.UTF8.GetBytes(edited)))).Content.ReadAsStringAsync();
        Assert.Contains(">new version<", changed);
        Assert.Contains("Added v3 as a draft", changed);

        await using var db = _factory.CreateDbContext();
        var prompt = await db.Prompts.SingleAsync(p => p.Id == id);
        Assert.Equal(3, prompt.CurrentVersion);
        Assert.Equal(1, prompt.ProductionVersion);
        Assert.Contains("in under 60 words", prompt.CurrentText);
        Assert.Equal(1, await db.EvalCases.CountAsync(c => c.PromptId == id));
    }

    [Fact]
    public async Task Folder_zip_round_trips_and_ignores_other_files()
    {
        var owner = await SignedInAsync("owner@test.local");
        await SeedAsync(owner);
        var zipBytes = await owner.GetByteArrayAsync("/Prompts/ExportFolder");

        using (var zip = new ZipArchive(new MemoryStream(zipBytes)))
            Assert.Equal("prompts/support-reply.yaml", Assert.Single(zip.Entries).FullName);

        // A repository folder also has files that are not prompts.
        using var withReadme = new MemoryStream();
        withReadme.Write(zipBytes);
        using (var zip = new ZipArchive(withReadme, ZipArchiveMode.Update, leaveOpen: true))
        {
            using var w = new StreamWriter(zip.CreateEntry("README.md").Open());
            w.Write("# Prompts");
        }

        var other = await SignedInAsync("other@test.local", "Other");
        var html = await (await UploadAsync(other, ("prompts.zip", withReadme.ToArray()))).Content.ReadAsStringAsync();
        Assert.Contains(">created<", html);
        Assert.DoesNotContain("alert-danger", html);
    }

    [Fact]
    public async Task Hand_written_file_with_unknown_category_is_skipped_with_the_reason()
    {
        var client = await SignedInAsync("owner@test.local");
        const string file = "prompts:\n  - title: Good\n    category: Learning\n    tool: Claude\n    text: Explain recursion with one short example.\n" +
                            "  - title: Bad\n    category: Poetry\n    tool: Claude\n    text: Write a short poem about the sea.\n";

        var html = await (await UploadAsync(client, ("mixed.yml", Encoding.UTF8.GetBytes(file)))).Content.ReadAsStringAsync();

        Assert.Contains(">created<", html);
        Assert.Contains("Unknown category &quot;Poetry&quot;", html);
        await using var db = _factory.CreateDbContext();
        Assert.Equal(new[] { "Good" }, await db.Prompts.Select(p => p.Title).ToListAsync());
    }

    [Fact]
    public async Task Broken_or_unsupported_files_are_reported()
    {
        var client = await SignedInAsync("owner@test.local");

        var html = await (await UploadAsync(client,
            ("broken.json", "{ \"title\": "u8.ToArray()),
            ("notes.txt", "hello"u8.ToArray()))).Content.ReadAsStringAsync();

        Assert.Contains("broken.json: not a valid JSON file", html);
        Assert.Contains("notes.txt: use a .yaml, .yml, .json or .zip file.", html);
    }

    [Fact]
    public async Task Import_needs_an_account()
    {
        var anonymous = _factory.CreateNoRedirectClient();

        Assert.Contains("/Account/Login", (await anonymous.GetAsync("/Prompts/Import")).Headers.Location!.OriginalString);
    }

    [Fact]
    public void Single_prompt_yaml_and_multi_prompt_yaml_both_read()
    {
        var single = PromptSerializer.Read("title: One\ncategory: Learning\ntool: Claude\ntext: Hello there, model.\n", isJson: false);
        var multi = PromptSerializer.Read("format: promptmanager/v1\nprompts:\n  - title: A\n  - title: B\n", isJson: false);

        Assert.Equal("Hello there, model.", Assert.Single(single).EffectiveVersions().Single().Text);
        Assert.Equal(new[] { "A", "B" }, multi.Select(p => p.Title));
    }
}
