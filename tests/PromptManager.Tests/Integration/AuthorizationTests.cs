using System.Net;
using Microsoft.EntityFrameworkCore;
using PromptManager.Tests.Infrastructure;

namespace PromptManager.Tests.Integration;

/// <summary>Who may do what: anonymous visitors, the author, another user and an admin.</summary>
public sealed class AuthorizationTests : IDisposable
{
    private readonly PromptManagerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient NewClient() => _factory.CreateNoRedirectClient();

    private static async Task<int> CreatePromptAsync(HttpClient client, string title = "Owned prompt")
    {
        var response = await client.PostFormAsync("/Prompts/Create", "/Prompts/Create", new Dictionary<string, string>
        {
            ["Title"] = title,
            ["PromptText"] = "Summarise the text in three bullet points.",
            ["Category"] = "Documentation",
            ["AiTool"] = "Claude"
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return int.Parse(response.Headers.Location!.OriginalString.Split('/').Last());
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, int id, string action) =>
        client.PostFormAsync($"/Prompts/Details/{id}", $"/Prompts/{action}", new Dictionary<string, string> { ["id"] = id.ToString() });

    // ---------- Anonymous ----------

    [Theory]
    [InlineData("/Prompts")]
    [InlineData("/Runs")]
    [InlineData("/Account/Login")]
    [InlineData("/Account/Register")]
    public async Task Reading_is_open_to_everyone(string url)
    {
        Assert.Equal(HttpStatusCode.OK, (await NewClient().GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Creating_requires_sign_in()
    {
        var response = await NewClient().GetAsync("/Prompts/Create");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Anonymous_visitor_sees_details_but_no_run_form()
    {
        var owner = NewClient();
        await owner.SignUpAsync("owner@test.local", "Owner");
        var id = await CreatePromptAsync(owner);

        var html = await NewClient().GetStringAsync($"/Prompts/Details/{id}");

        Assert.Contains("Owned prompt", html);
        Assert.Contains("to run this prompt on a model", html);
        Assert.DoesNotContain("action=\"/Prompts/Run\"", html);
        Assert.DoesNotContain("action=\"/Prompts/Archive\"", html);
    }

    // ---------- Author ----------

    [Fact]
    public async Task Author_name_comes_from_the_account_not_the_form()
    {
        var owner = NewClient();
        await owner.SignUpAsync("owner@test.local", "Olena");

        var response = await owner.PostFormAsync("/Prompts/Create", "/Prompts/Create", new Dictionary<string, string>
        {
            ["Title"] = "Spoof attempt",
            ["PromptText"] = "Summarise the text in three bullet points.",
            ["Category"] = "Documentation",
            ["AiTool"] = "Claude",
            ["Author"] = "Someone else",
            ["OwnerId"] = "someone-elses-id"
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var db = _factory.CreateDbContext();
        var prompt = await db.Prompts.SingleAsync();
        var user = await db.Users.SingleAsync();
        Assert.Equal("Olena", prompt.Author);
        Assert.Equal(user.Id, prompt.OwnerId);
    }

    [Fact]
    public async Task Author_can_edit_archive_restore_and_delete()
    {
        var owner = NewClient();
        await owner.SignUpAsync("owner@test.local");
        var id = await CreatePromptAsync(owner);

        var edit = await owner.PostFormAsync($"/Prompts/Edit/{id}", "/Prompts/Edit", new Dictionary<string, string>
        {
            ["Id"] = id.ToString(), ["Title"] = "Renamed", ["Description"] = "New description",
            ["Category"] = "Testing", ["AiTool"] = "Gemini"
        });
        Assert.Equal(HttpStatusCode.Redirect, edit.StatusCode);

        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(owner, id, "Archive")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(owner, id, "Unarchive")).StatusCode);

        await using (var db = _factory.CreateDbContext())
        {
            var prompt = await db.Prompts.SingleAsync(p => p.Id == id);
            Assert.Equal(("Renamed", "Testing", "Gemini", false), (prompt.Title, prompt.Category, prompt.AiTool, prompt.IsArchived));
        }

        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(owner, id, "Delete")).StatusCode);
        await using (var db = _factory.CreateDbContext())
        {
            Assert.False(await db.Prompts.AnyAsync());
            Assert.False(await db.PromptVersions.AnyAsync());
        }
    }

    // ---------- Another user ----------

    [Theory]
    [InlineData("Archive")]
    [InlineData("Delete")]
    public async Task Another_user_cannot_archive_or_delete(string action)
    {
        var owner = NewClient();
        await owner.SignUpAsync("owner@test.local");
        var id = await CreatePromptAsync(owner);
        var other = NewClient();
        await other.SignUpAsync("other@test.local");

        var response = await PostAsync(other, id, action);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/AccessDenied", response.Headers.Location!.OriginalString);
        await using var db = _factory.CreateDbContext();
        Assert.False((await db.Prompts.SingleAsync()).IsArchived);
    }

    [Fact]
    public async Task Another_user_cannot_open_edit_or_new_version()
    {
        var owner = NewClient();
        await owner.SignUpAsync("owner@test.local");
        var id = await CreatePromptAsync(owner);
        var other = NewClient();
        await other.SignUpAsync("other@test.local");

        Assert.Contains("/Account/AccessDenied", (await other.GetAsync($"/Prompts/Edit/{id}")).Headers.Location!.OriginalString);
        Assert.Contains("/Account/AccessDenied", (await other.GetAsync($"/Prompts/CreateVersion/{id}")).Headers.Location!.OriginalString);
        Assert.DoesNotContain("action=\"/Prompts/Delete\"", await other.GetStringAsync($"/Prompts/Details/{id}"));
    }

    [Fact]
    public async Task Each_account_rates_once_whatever_name_it_types()
    {
        var owner = NewClient();
        await owner.SignUpAsync("owner@test.local");
        var id = await CreatePromptAsync(owner);
        var rater = NewClient();
        await rater.SignUpAsync("rater@test.local");

        var first = await rater.PostFormAsync($"/Prompts/Rate/{id}", "/Prompts/Rate", new Dictionary<string, string>
        {
            ["PromptId"] = id.ToString(), ["Rating"] = "5", ["UserName"] = "alias-1"
        });
        var second = await rater.PostFormAsync($"/Prompts/Rate/{id}", "/Prompts/Rate", new Dictionary<string, string>
        {
            ["PromptId"] = id.ToString(), ["Rating"] = "1", ["UserName"] = "alias-2"
        });

        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Contains("You have already rated this prompt.", await second.Content.ReadAsStringAsync());
    }

    // ---------- Admin ----------

    [Fact]
    public async Task Admin_from_configuration_can_manage_any_prompt()
    {
        var owner = NewClient();
        await owner.SignUpAsync("owner@test.local");
        var id = await CreatePromptAsync(owner);
        var admin = NewClient();
        await admin.SignUpAsync(PromptManagerFactory.AdminEmail, "Admin");

        Assert.Contains(">admin<", await admin.GetStringAsync("/Prompts"));
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(admin, id, "Archive")).StatusCode);
        await using var db = _factory.CreateDbContext();
        Assert.True((await db.Prompts.SingleAsync()).IsArchived);
    }

    // ---------- Accounts ----------

    [Fact]
    public async Task Sign_out_and_sign_in_again()
    {
        var client = NewClient();
        await client.SignUpAsync("owner@test.local", "Owner");
        await client.SignOutAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Prompts/Create")).StatusCode);

        var login = await client.PostFormAsync("/Account/Login", "/Account/Login", new Dictionary<string, string>
        {
            ["Email"] = "owner@test.local", ["Password"] = PromptManagerFactory.Password
        });

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Prompts/Create")).StatusCode);
    }

    [Fact]
    public async Task Wrong_password_gives_a_generic_error()
    {
        var client = NewClient();
        await client.SignUpAsync("owner@test.local");
        await client.SignOutAsync();

        var login = await client.PostFormAsync("/Account/Login", "/Account/Login", new Dictionary<string, string>
        {
            ["Email"] = "owner@test.local", ["Password"] = "Wrong-password-1"
        });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("Invalid e-mail or password.", await login.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_does_not_redirect_to_another_site()
    {
        var client = NewClient();
        await client.SignUpAsync("owner@test.local");
        await client.SignOutAsync();

        var login = await client.PostFormAsync("/Account/Login", "/Account/Login", new Dictionary<string, string>
        {
            ["Email"] = "owner@test.local", ["Password"] = PromptManagerFactory.Password, ["ReturnUrl"] = "https://evil.example/"
        });

        Assert.Equal("/", login.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Weak_password_and_duplicate_email_are_rejected()
    {
        var client = NewClient();
        var weak = await client.PostFormAsync("/Account/Register", "/Account/Register", new Dictionary<string, string>
        {
            ["Email"] = "weak@test.local", ["DisplayName"] = "Weak", ["Password"] = "short", ["ConfirmPassword"] = "short"
        });
        Assert.Equal(HttpStatusCode.OK, weak.StatusCode);

        await client.SignUpAsync("taken@test.local");
        await client.SignOutAsync();
        var duplicate = await client.PostFormAsync("/Account/Register", "/Account/Register", new Dictionary<string, string>
        {
            ["Email"] = "taken@test.local", ["DisplayName"] = "Again",
            ["Password"] = PromptManagerFactory.Password, ["ConfirmPassword"] = PromptManagerFactory.Password
        });
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.Users.CountAsync());
    }
}
