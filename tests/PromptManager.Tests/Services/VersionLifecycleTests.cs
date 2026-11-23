using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PromptManager.DTOs;
using PromptManager.Models;
using PromptManager.Services;
using PromptManager.Tests.Infrastructure;

namespace PromptManager.Tests.Services;

public sealed class VersionLifecycleTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private VersionLifecycle Lifecycle() => new(_database.CreateContext(), NullLogger<VersionLifecycle>.Instance);

    private PromptService Prompts() =>
        new(_database.CreateContext(), new ConfigurationBuilder().Build(), NullLogger<PromptService>.Instance);

    /// <summary>A prompt with <paramref name="versions"/> versions; v1 is production.</summary>
    private async Task<int> SeedAsync(int versions = 2)
    {
        var id = await Prompts().CreateAsync(new CreatePromptDto
        {
            Title = "T", PromptText = "Version one of the prompt text.", Category = "Testing", AiTool = "Claude", Author = "alice"
        });
        for (var v = 2; v <= versions; v++)
            await Prompts().CreateVersionAsync(new CreatePromptVersionDto { PromptId = id, PromptText = $"Version {v} of the prompt text.", CreatedBy = "alice" });
        return id;
    }

    private async Task AddCaseAsync(int promptId)
    {
        using var db = _database.CreateContext();
        db.EvalCases.Add(new EvalCase { PromptId = promptId, Name = "c", MustContain = "x" });
        await db.SaveChangesAsync();
    }

    private async Task<int> AddEvalAsync(int promptId, int version, int passed, int total, string model = "m")
    {
        using var db = _database.CreateContext();
        var run = new EvalRun
        {
            PromptId = promptId, VersionNumber = version, Model = model, Status = EvalRunStatus.Completed,
            TotalCases = total, CompletedCases = total, PassedCases = passed, RunBy = "t"
        };
        db.EvalRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private async Task<(int? Production, Dictionary<int, VersionStatus> Statuses)> StateAsync(int promptId)
    {
        using var db = _database.CreateContext();
        var prompt = await db.Prompts.Include(p => p.Versions).SingleAsync(p => p.Id == promptId);
        return (prompt.ProductionVersion, prompt.Versions.ToDictionary(v => v.VersionNumber, v => v.Status));
    }

    [Fact]
    public async Task First_version_is_production_and_later_ones_are_drafts()
    {
        var id = await SeedAsync(versions: 2);

        var (production, statuses) = await StateAsync(id);

        Assert.Equal(1, production);
        Assert.Equal(VersionStatus.Production, statuses[1]);
        Assert.Equal(VersionStatus.Draft, statuses[2]);
    }

    [Fact]
    public async Task Without_cases_promotion_is_allowed_and_the_old_version_becomes_tested()
    {
        var id = await SeedAsync();

        var result = await Lifecycle().PromoteAsync(id, 2, "bob", overrideRegression: false);

        Assert.True(result.Promoted, result.Message);
        Assert.Equal("Promoted v2 in production.", result.Message);
        var (production, statuses) = await StateAsync(id);
        Assert.Equal(2, production);
        Assert.Equal((VersionStatus.Tested, VersionStatus.Production), (statuses[1], statuses[2]));
    }

    [Fact]
    public async Task With_cases_a_version_needs_an_eval_first()
    {
        var id = await SeedAsync();
        await AddCaseAsync(id);

        var result = await Lifecycle().PromoteAsync(id, 2, "bob", overrideRegression: false);

        Assert.False(result.Promoted);
        Assert.StartsWith("Run an eval of v2", result.Message);
        Assert.Equal(1, (await StateAsync(id)).Production);
    }

    [Fact]
    public async Task A_regression_on_the_same_model_blocks_promotion()
    {
        var id = await SeedAsync();
        await AddCaseAsync(id);
        await AddEvalAsync(id, 1, passed: 4, total: 4);
        await AddEvalAsync(id, 2, passed: 3, total: 4);

        var result = await Lifecycle().PromoteAsync(id, 2, "bob", overrideRegression: false);

        Assert.False(result.Promoted);
        Assert.True(result.BlockedByRegression);
        Assert.Contains("3/4", result.Message);
        Assert.Contains("4/4", result.Message);
    }

    [Fact]
    public async Task An_override_promotes_and_records_the_regression()
    {
        var id = await SeedAsync();
        await AddCaseAsync(id);
        await AddEvalAsync(id, 1, passed: 4, total: 4);
        await AddEvalAsync(id, 2, passed: 3, total: 4);

        var result = await Lifecycle().PromoteAsync(id, 2, "admin", overrideRegression: true);

        Assert.True(result.Promoted);
        Assert.Contains("worse", result.Message);
        using var db = _database.CreateContext();
        var v2 = await db.PromptVersions.SingleAsync(v => v.PromptId == id && v.VersionNumber == 2);
        Assert.True(v2.PromotedOverRegression);
        Assert.Equal("admin", v2.PromotedBy);
    }

    [Fact]
    public async Task Equal_or_better_results_pass_and_other_models_do_not_count()
    {
        var id = await SeedAsync();
        await AddCaseAsync(id);
        await AddEvalAsync(id, 1, passed: 2, total: 4, model: "other");   // not comparable
        await AddEvalAsync(id, 1, passed: 3, total: 4);
        await AddEvalAsync(id, 2, passed: 3, total: 4);

        Assert.True((await Lifecycle().PromoteAsync(id, 2, "bob", false)).Promoted);
    }

    [Fact]
    public async Task Rolling_back_to_an_older_version_says_so()
    {
        var id = await SeedAsync();
        await Lifecycle().PromoteAsync(id, 2, "bob", false);

        var result = await Lifecycle().PromoteAsync(id, 1, "bob", false);

        Assert.Equal("Rolled back to v1 in production.", result.Message);
        Assert.Equal(1, (await StateAsync(id)).Production);
    }

    [Fact]
    public async Task Promoting_the_production_version_or_a_missing_one_is_refused()
    {
        var id = await SeedAsync();

        Assert.Equal("v1 is already in production.", (await Lifecycle().PromoteAsync(id, 1, "b", false)).Message);
        Assert.Equal("Version 9 does not exist.", (await Lifecycle().PromoteAsync(id, 9, "b", false)).Message);
        Assert.Equal("Prompt not found.", (await Lifecycle().PromoteAsync(999, 1, "b", false)).Message);
    }

    [Theory]
    [InlineData(3, 3, VersionStatus.Tested)]
    [InlineData(2, 3, VersionStatus.Draft)]
    public async Task A_fully_passing_eval_marks_a_draft_as_tested(int passed, int total, VersionStatus expected)
    {
        var id = await SeedAsync();
        var evalId = await AddEvalAsync(id, 2, passed, total);

        await Lifecycle().MarkTestedIfPassedAsync(evalId);

        Assert.Equal(expected, (await StateAsync(id)).Statuses[2]);
    }

    [Fact]
    public async Task A_passing_eval_does_not_demote_the_production_version()
    {
        var id = await SeedAsync();
        var evalId = await AddEvalAsync(id, 1, passed: 2, total: 2);

        await Lifecycle().MarkTestedIfPassedAsync(evalId);

        Assert.Equal(VersionStatus.Production, (await StateAsync(id)).Statuses[1]);
    }
}
