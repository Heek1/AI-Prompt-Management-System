using System.Net;
using Microsoft.EntityFrameworkCore;
using PromptManager.Models;
using PromptManager.Tests.Infrastructure;

namespace PromptManager.Tests.Integration;

/// <summary>Tags and collections (C3), and the admin-managed category and tool lists.</summary>
public sealed class TagsAndListsTests : IDisposable
{
    private readonly PromptManagerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> SignedInAsync(string email, string name = "Tester")
    {
        var client = _factory.CreateNoRedirectClient();
        await client.SignUpAsync(email, name);
        return client;
    }

    private static async Task<int> CreateAsync(HttpClient client, string title, string tags, string category = "Learning")
    {
        var response = await client.PostFormAsync("/Prompts/Create", "/Prompts/Create", new Dictionary<string, string>
        {
            ["Title"] = title,
            ["PromptText"] = "Summarise the following text in three bullet points.",
            ["Category"] = category,
            ["AiTool"] = "Claude",
            ["Tags"] = tags
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return int.Parse(response.Headers.Location!.OriginalString.Split('/').Last());
    }

    [Fact]
    public async Task Tags_are_saved_shown_and_shared_between_prompts()
    {
        var client = await SignedInAsync("owner@test.local");
        var first = await CreateAsync(client, "First", "Support, email");
        await CreateAsync(client, "Second", "support");

        var details = await client.GetStringAsync($"/Prompts/Details/{first}");
        Assert.Contains("#Support", details);
        Assert.Contains("href=\"/?tag=email\"", details);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(2, await db.Tags.CountAsync());
        Assert.Equal(2, await db.PromptTags.CountAsync(pt => pt.Tag!.Slug == "support"));
    }

    [Fact]
    public async Task Library_filters_by_tag_and_lists_collections_with_counts()
    {
        var client = await SignedInAsync("owner@test.local");
        await CreateAsync(client, "Tagged one", "support");
        await CreateAsync(client, "Tagged two", "support, en");
        await CreateAsync(client, "Untagged", "");

        var all = await client.GetStringAsync("/");
        Assert.Contains("#support <span>2</span>", all);
        Assert.Contains("#en <span>1</span>", all);

        var collection = await client.GetStringAsync("/?tag=support");
        Assert.Contains("Tagged one", collection);
        Assert.Contains("Tagged two", collection);
        Assert.DoesNotContain("Untagged", collection);
        Assert.Contains("<h1>#support</h1>", collection);
    }

    [Fact]
    public async Task Editing_replaces_the_tags()
    {
        var client = await SignedInAsync("owner@test.local");
        var id = await CreateAsync(client, "Retagged", "old, kept");

        var edit = await client.GetStringAsync($"/Prompts/Edit/{id}");
        Assert.Contains("value=\"kept, old\"", edit);

        var response = await client.PostFormAsync($"/Prompts/Edit/{id}", "/Prompts/Edit", new Dictionary<string, string>
        {
            ["Id"] = id.ToString(), ["Title"] = "Retagged", ["Category"] = "Learning", ["AiTool"] = "Claude", ["Tags"] = "kept, new"
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        await using var db = _factory.CreateDbContext();
        var slugs = await db.PromptTags.Where(pt => pt.PromptId == id).Select(pt => pt.Tag!.Slug).OrderBy(s => s).ToListAsync();
        Assert.Equal(new[] { "kept", "new" }, slugs);
    }

    [Fact]
    public async Task Lists_page_is_for_admins_only()
    {
        var user = await SignedInAsync("user@test.local");
        Assert.Contains("/Account/AccessDenied", (await user.GetAsync("/Lists")).Headers.Location!.OriginalString);
        Assert.DoesNotContain("/Lists\"", await user.GetStringAsync("/Prompts"));

        var admin = await SignedInAsync(PromptManagerFactory.AdminEmail, "Admin");
        var html = await admin.GetStringAsync("/Lists");
        Assert.Contains("Business Analysis", html);
        Assert.Contains("Mistral", html);
    }

    [Fact]
    public async Task Admin_adds_a_category_that_prompts_can_then_use()
    {
        var admin = await SignedInAsync(PromptManagerFactory.AdminEmail, "Admin");
        var before = await admin.PostFormAsync("/Prompts/Create", "/Prompts/Create", new Dictionary<string, string>
        {
            ["Title"] = "Early", ["PromptText"] = "Summarise the following text in three bullet points.",
            ["Category"] = "Data Analysis", ["AiTool"] = "Claude"
        });
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        var add = await admin.PostFormAsync("/Lists", "/Lists/Add", new Dictionary<string, string>
        {
            ["kind"] = nameof(LookupKind.Category), ["name"] = "Data Analysis"
        });
        Assert.Equal(HttpStatusCode.Redirect, add.StatusCode);

        await CreateAsync(admin, "Later", "", category: "Data Analysis");
        await using var db = _factory.CreateDbContext();
        // The seed takes ids 1 to 13; a new entry must not collide with them.
        Assert.True((await db.LookupValues.SingleAsync(v => v.Name == "Data Analysis")).Id > 13);
    }

    [Fact]
    public async Task Removed_category_stays_on_existing_prompts_and_their_edit_form()
    {
        var admin = await SignedInAsync(PromptManagerFactory.AdminEmail, "Admin");
        var id = await CreateAsync(admin, "Old category", "", category: "UI Design");

        int lookupId;
        await using (var db = _factory.CreateDbContext())
            lookupId = (await db.LookupValues.SingleAsync(v => v.Name == "UI Design")).Id;
        var remove = await admin.PostFormAsync("/Lists", "/Lists/Remove", new Dictionary<string, string> { ["id"] = lookupId.ToString() });
        Assert.Equal(HttpStatusCode.Redirect, remove.StatusCode);

        // Saving the prompt with its old category is still allowed...
        var save = await admin.PostFormAsync($"/Prompts/Edit/{id}", "/Prompts/Edit", new Dictionary<string, string>
        {
            ["Id"] = id.ToString(), ["Title"] = "Renamed", ["Category"] = "UI Design", ["AiTool"] = "Claude"
        });
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);

        // ...but a new prompt cannot pick it.
        var create = await admin.PostFormAsync("/Prompts/Create", "/Prompts/Create", new Dictionary<string, string>
        {
            ["Title"] = "New", ["PromptText"] = "Summarise the following text in three bullet points.",
            ["Category"] = "UI Design", ["AiTool"] = "Claude"
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
    }

    [Fact]
    public async Task Duplicate_list_entry_is_refused()
    {
        var admin = await SignedInAsync(PromptManagerFactory.AdminEmail, "Admin");
        await admin.PostFormAsync("/Lists", "/Lists/Add", new Dictionary<string, string> { ["kind"] = "Tool", ["name"] = "claude" });

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.LookupValues.CountAsync(v => v.Kind == LookupKind.Tool && v.Name.ToLower() == "claude"));
    }
}
