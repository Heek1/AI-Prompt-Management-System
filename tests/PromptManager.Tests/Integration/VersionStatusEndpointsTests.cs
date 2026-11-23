using System.Net;
using Microsoft.EntityFrameworkCore;
using PromptManager.Models;
using PromptManager.Tests.Infrastructure;

namespace PromptManager.Tests.Integration;

public sealed class VersionStatusEndpointsTests : IDisposable
{
    private readonly PromptManagerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<(HttpClient Owner, int Id)> PromptWithTwoVersionsAsync()
    {
        var owner = _factory.CreateNoRedirectClient();
        await owner.SignUpAsync("owner@test.local");
        var create = await owner.PostFormAsync("/Prompts/Create", "/Prompts/Create", new Dictionary<string, string>
        {
            ["Title"] = "T", ["PromptText"] = "Summarise the text in three bullet points.", ["Category"] = "Learning", ["AiTool"] = "Claude"
        });
        var id = int.Parse(create.Headers.Location!.OriginalString.Split('/').Last());
        await owner.PostFormAsync($"/Prompts/CreateVersion/{id}", "/Prompts/CreateVersion", new Dictionary<string, string>
        {
            ["PromptId"] = id.ToString(), ["PromptText"] = "Summarise the text in five bullet points."
        });
        return (owner, id);
    }

    private static Task<HttpResponseMessage> PromoteAsync(HttpClient client, int id, int version, bool force = false) =>
        client.PostFormAsync($"/Prompts/Details/{id}", "/Prompts/Promote", new Dictionary<string, string>
        {
            ["id"] = id.ToString(), ["versionNumber"] = version.ToString(), ["overrideRegression"] = force ? "true" : "false"
        });

    private async Task<int?> ProductionAsync(int id)
    {
        await using var db = _factory.CreateDbContext();
        return (await db.Prompts.SingleAsync(p => p.Id == id)).ProductionVersion;
    }

    [Fact]
    public async Task Details_shows_statuses_and_a_promote_button_for_the_owner()
    {
        var (owner, id) = await PromptWithTwoVersionsAsync();

        var html = await owner.GetStringAsync($"/Prompts/Details/{id}");

        Assert.Contains("data-status=\"Production\"", html);
        Assert.Contains("data-status=\"Draft\"", html);
        Assert.Contains("action=\"/Prompts/Promote\"", html);
        Assert.Contains("data-testid=\"production-version\">v1<", html);
    }

    [Fact]
    public async Task Owner_promotes_and_rolls_back()
    {
        var (owner, id) = await PromptWithTwoVersionsAsync();

        await PromoteAsync(owner, id, 2);
        Assert.Equal(2, await ProductionAsync(id));

        await PromoteAsync(owner, id, 1);
        Assert.Equal(1, await ProductionAsync(id));
        Assert.Contains("Rolled back to v1", await owner.GetStringAsync($"/Prompts/Details/{id}"));
    }

    [Fact]
    public async Task Another_user_cannot_promote()
    {
        var (_, id) = await PromptWithTwoVersionsAsync();
        var other = _factory.CreateNoRedirectClient();
        await other.SignUpAsync("other@test.local");

        var response = await PromoteAsync(other, id, 2);

        Assert.Contains("/Account/AccessDenied", response.Headers.Location!.OriginalString);
        Assert.Equal(1, await ProductionAsync(id));
    }

    [Fact]
    public async Task Only_an_admin_can_override_a_regression()
    {
        var (owner, id) = await PromptWithTwoVersionsAsync();
        await using (var db = _factory.CreateDbContext())
        {
            db.EvalCases.Add(new EvalCase { PromptId = id, Name = "c", MustContain = "x" });
            db.EvalRuns.AddRange(
                new EvalRun { PromptId = id, VersionNumber = 1, Model = "echo", Status = EvalRunStatus.Completed, TotalCases = 2, CompletedCases = 2, PassedCases = 2, RunBy = "t" },
                new EvalRun { PromptId = id, VersionNumber = 2, Model = "echo", Status = EvalRunStatus.Completed, TotalCases = 2, CompletedCases = 2, PassedCases = 1, RunBy = "t" });
            await db.SaveChangesAsync();
        }

        // The owner is blocked even when asking to override.
        await PromoteAsync(owner, id, 2, force: true);
        Assert.Equal(1, await ProductionAsync(id));
        Assert.DoesNotContain("Promote anyway", await owner.GetStringAsync($"/Prompts/Details/{id}"));

        // An admin sees the override after being blocked, and it works.
        var admin = _factory.CreateNoRedirectClient();
        await admin.SignUpAsync(PromptManagerFactory.AdminEmail, "Admin");
        await PromoteAsync(admin, id, 2);
        Assert.Contains("Promote anyway", await admin.GetStringAsync($"/Prompts/Details/{id}"));

        await PromoteAsync(admin, id, 2, force: true);
        Assert.Equal(2, await ProductionAsync(id));
        Assert.Contains("over a regression", await admin.GetStringAsync($"/Prompts/Details/{id}"));
    }
}
