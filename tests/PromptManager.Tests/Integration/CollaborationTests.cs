using System.Net;
using Microsoft.EntityFrameworkCore;
using PromptManager.Tests.Infrastructure;

namespace PromptManager.Tests.Integration;

/// <summary>C6: comments on versions, watching and the inbox.</summary>
public sealed class CollaborationTests : IDisposable
{
    private readonly PromptManagerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> SignedInAsync(string email, string name)
    {
        var client = _factory.CreateNoRedirectClient();
        await client.SignUpAsync(email, name);
        return client;
    }

    private static async Task<int> CreateAsync(HttpClient client)
    {
        var response = await client.PostFormAsync("/Prompts/Create", "/Prompts/Create", new Dictionary<string, string>
        {
            ["Title"] = "Shared prompt", ["PromptText"] = "Summarise the following text in three bullet points.",
            ["Category"] = "Learning", ["AiTool"] = "Claude"
        });
        return int.Parse(response.Headers.Location!.OriginalString.Split('/').Last());
    }

    private static Task<HttpResponseMessage> CommentAsync(HttpClient client, int id, int version, string body) =>
        client.PostFormAsync($"/Prompts/Details/{id}", "/Prompts/Comment", new Dictionary<string, string>
        {
            ["promptId"] = id.ToString(), ["versionNumber"] = version.ToString(), ["body"] = body
        });

    [Fact]
    public async Task Comment_is_shown_on_its_version_and_notifies_the_author()
    {
        var owner = await SignedInAsync("owner@test.local", "Olena");
        var id = await CreateAsync(owner);
        var reader = await SignedInAsync("reader@test.local", "Petro");

        var response = await CommentAsync(reader, id, 1, "Too long for <b>mobile</b>.");
        Assert.Equal($"/Prompts/Details/{id}#v1", response.Headers.Location!.OriginalString);

        var page = await reader.GetStringAsync($"/Prompts/Details/{id}");
        Assert.Contains("1 comment", page);
        Assert.Contains("Too long for &lt;b&gt;mobile&lt;/b&gt;.", page); // encoded, not HTML

        // The author watches their prompt and gets one unread notice.
        Assert.Contains("aria-label=\"1 unread\"", await owner.GetStringAsync("/Prompts"));
        var inbox = await owner.GetStringAsync("/Inbox");
        Assert.Contains("Petro commented on v1 of Shared prompt", inbox);
        Assert.Contains($"href=\"/Prompts/Details/{id}#v1\"", inbox);
        // Opening the inbox reads it.
        Assert.DoesNotContain("unread\"", await owner.GetStringAsync("/Prompts"));
    }

    [Fact]
    public async Task Watchers_hear_about_new_versions_and_promotions_but_not_about_their_own_actions()
    {
        var owner = await SignedInAsync("owner@test.local", "Olena");
        var id = await CreateAsync(owner);
        var watcher = await SignedInAsync("watcher@test.local", "Petro");
        await watcher.PostFormAsync($"/Prompts/Details/{id}", "/Prompts/Watch", new Dictionary<string, string>
        {
            ["promptId"] = id.ToString(), ["watch"] = "true"
        });
        Assert.Contains(">Watching<", await watcher.GetStringAsync($"/Prompts/Details/{id}"));

        await owner.PostFormAsync($"/Prompts/CreateVersion/{id}", "/Prompts/CreateVersion", new Dictionary<string, string>
        {
            ["PromptId"] = id.ToString(), ["PromptText"] = "Summarise the text below in two short bullet points.", ["ChangeNote"] = "Shorter"
        });
        await owner.PostFormAsync($"/Prompts/Details/{id}", "/Prompts/Promote", new Dictionary<string, string>
        {
            ["id"] = id.ToString(), ["versionNumber"] = "2", ["overrideRegression"] = "false"
        });

        var inbox = await watcher.GetStringAsync("/Inbox");
        Assert.Contains("Olena added v2 of Shared prompt: Shorter", inbox);
        Assert.Contains("Olena put v2 of Shared prompt in production.", inbox);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.Notifications.CountAsync(n => n.Text.StartsWith("Olena") && n.UserId != db.Users.Single(u => u.Email == "watcher@test.local").Id));
    }

    [Fact]
    public async Task Only_the_author_of_a_comment_or_an_admin_can_delete_it()
    {
        var owner = await SignedInAsync("owner@test.local", "Olena");
        var id = await CreateAsync(owner);
        await CommentAsync(owner, id, 1, "Mine");
        int commentId;
        await using (var db = _factory.CreateDbContext())
            commentId = (await db.VersionComments.SingleAsync()).Id;

        var form = new Dictionary<string, string> { ["promptId"] = id.ToString(), ["commentId"] = commentId.ToString(), ["versionNumber"] = "1" };
        var other = await SignedInAsync("other@test.local", "Other");
        Assert.Contains("/Account/AccessDenied", (await other.PostFormAsync($"/Prompts/Details/{id}", "/Prompts/DeleteComment", form)).Headers.Location!.OriginalString);

        var admin = await SignedInAsync(PromptManagerFactory.AdminEmail, "Admin");
        Assert.Equal(HttpStatusCode.Redirect, (await admin.PostFormAsync($"/Prompts/Details/{id}", "/Prompts/DeleteComment", form)).StatusCode);
        await using var after = _factory.CreateDbContext();
        Assert.Equal(0, await after.VersionComments.CountAsync());
    }

    [Theory]
    [InlineData("1", "   ", "Write something first.")]
    [InlineData("9", "Hello", "Version 9 does not exist.")]
    public async Task Empty_comments_and_unknown_versions_are_refused(string version, string body, string message)
    {
        var owner = await SignedInAsync("owner@test.local", "Olena");
        var id = await CreateAsync(owner);

        var response = await CommentAsync(owner, id, int.Parse(version), body);

        Assert.Contains(message, await owner.GetStringAsync(response.Headers.Location!.OriginalString.Split('#')[0]));
        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.VersionComments.CountAsync());
    }

    [Fact]
    public async Task Anonymous_visitors_see_comments_but_cannot_post()
    {
        var owner = await SignedInAsync("owner@test.local", "Olena");
        var id = await CreateAsync(owner);
        await CommentAsync(owner, id, 1, "Visible to all");

        var anonymous = _factory.CreateNoRedirectClient();
        var page = await anonymous.GetStringAsync($"/Prompts/Details/{id}");
        Assert.Contains("Visible to all", page);
        Assert.DoesNotContain("action=\"/Prompts/Comment\"", page);
        Assert.Contains("/Account/Login", (await anonymous.GetAsync("/Inbox")).Headers.Location!.OriginalString);
    }
}
