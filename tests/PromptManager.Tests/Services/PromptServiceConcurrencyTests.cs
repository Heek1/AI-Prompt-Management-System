using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PromptManager.Data;
using PromptManager.DTOs;
using PromptManager.Models;
using PromptManager.Services;
using PromptManager.Tests.Infrastructure;

namespace PromptManager.Tests.Services;

/// <summary>
/// Races are simulated deterministically: a competing write is injected through
/// a second context in the gap between the service's read and its SaveChanges.
/// </summary>
public sealed class PromptServiceConcurrencyTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private static PromptService CreateService(AppDbContext db) =>
        new(db, new ConfigurationBuilder().Build(), NullLogger<PromptService>.Instance);

    private async Task<int> SeedPromptAsync()
    {
        using var db = _database.CreateContext();
        return await CreateService(db).CreateAsync(new CreatePromptDto
        {
            Title = "Seed",
            PromptText = "Initial prompt text, long enough.",
            Category = "Testing",
            AiTool = "Claude",
            Author = "alice"
        });
    }

    /// <summary>Runs <paramref name="competingWrite"/> once, just before the first SaveChanges of <paramref name="db"/>.</summary>
    private void BeforeFirstSave(AppDbContext db, Action<AppDbContext> competingWrite)
    {
        var fired = false;
        db.SavingChanges += (_, _) =>
        {
            if (fired) return;
            fired = true;
            using var other = _database.CreateContext();
            competingWrite(other);
        };
    }

    [Fact]
    public async Task CreateAsync_saves_prompt_and_first_version_in_one_SaveChanges()
    {
        using var db = _database.CreateContext();
        var saves = 0;
        db.SavingChanges += (_, _) => saves++;

        await CreateService(db).CreateAsync(new CreatePromptDto
        {
            Title = "Atomic", PromptText = "Prompt text that is long enough.", Category = "Testing", AiTool = "Claude", Author = "a"
        });

        Assert.Equal(1, saves);
        using var check = _database.CreateContext();
        Assert.Equal(1, await check.PromptVersions.CountAsync());
    }

    [Fact]
    public async Task Database_rejects_duplicate_version_numbers_for_one_prompt()
    {
        var id = await SeedPromptAsync();
        using var db = _database.CreateContext();
        db.PromptVersions.Add(new PromptVersion { PromptId = id, VersionNumber = 1, PromptText = "dup", CreatedBy = "x" });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task CreateVersionAsync_retries_when_another_writer_added_a_version_first()
    {
        var id = await SeedPromptAsync();
        using var db = _database.CreateContext();
        BeforeFirstSave(db, other =>
        {
            var prompt = other.Prompts.Single(p => p.Id == id);
            prompt.CurrentVersion = 2;
            prompt.CurrentText = "Competing version two text.";
            prompt.ConcurrencyStamp = Guid.NewGuid().ToString();
            other.PromptVersions.Add(new PromptVersion { PromptId = id, VersionNumber = 2, PromptText = prompt.CurrentText, CreatedBy = "bob" });
            other.SaveChanges();
        });

        var ok = await CreateService(db).CreateVersionAsync(new CreatePromptVersionDto
        {
            PromptId = id, PromptText = "Our version, written second.", CreatedBy = "carol"
        });

        Assert.True(ok);
        using var check = _database.CreateContext();
        var versions = await check.PromptVersions.Where(v => v.PromptId == id).OrderBy(v => v.VersionNumber).ToListAsync();
        Assert.Equal(new[] { 1, 2, 3 }, versions.Select(v => v.VersionNumber));
        Assert.Equal("carol", versions[2].CreatedBy);
        var prompt = await check.Prompts.SingleAsync(p => p.Id == id);
        Assert.Equal(3, prompt.CurrentVersion);
        Assert.Equal("Our version, written second.", prompt.CurrentText);
    }

    [Fact]
    public async Task CreateVersionAsync_refuses_when_prompt_was_archived_concurrently()
    {
        var id = await SeedPromptAsync();
        using var db = _database.CreateContext();
        BeforeFirstSave(db, other => CreateService(other).ArchiveAsync(id).GetAwaiter().GetResult());

        var ok = await CreateService(db).CreateVersionAsync(new CreatePromptVersionDto
        {
            PromptId = id, PromptText = "Edit racing with an archive.", CreatedBy = "carol"
        });

        Assert.False(ok);
        using var check = _database.CreateContext();
        Assert.Equal(1, await check.PromptVersions.CountAsync(v => v.PromptId == id));
    }

    [Fact]
    public async Task RateAsync_reports_already_rated_when_a_concurrent_request_won()
    {
        var id = await SeedPromptAsync();
        using var db = _database.CreateContext();
        BeforeFirstSave(db, other =>
        {
            other.PromptRatings.Add(new PromptRating { PromptId = id, UserName = "bob", Rating = 5 });
            other.SaveChanges();
        });
        var service = CreateService(db);

        var (success, error) = await service.RateAsync(new RatePromptDto { PromptId = id, UserName = "bob", Rating = 1 });

        Assert.False(success);
        Assert.Equal("You have already rated this prompt.", error);

        // The same scoped service must still work after the conflict.
        var second = await service.RateAsync(new RatePromptDto { PromptId = id, UserName = "dave", Rating = 3 });
        Assert.True(second.success);
        using var check = _database.CreateContext();
        Assert.Equal(4, (await check.Prompts.SingleAsync(p => p.Id == id)).AverageRating);
    }

    [Fact]
    public async Task ArchiveAsync_changes_the_concurrency_stamp()
    {
        var id = await SeedPromptAsync();
        string before;
        using (var db = _database.CreateContext())
            before = (await db.Prompts.SingleAsync(p => p.Id == id)).ConcurrencyStamp;

        using (var db = _database.CreateContext())
            await CreateService(db).ArchiveAsync(id);

        using var check = _database.CreateContext();
        Assert.NotEqual(before, (await check.Prompts.SingleAsync(p => p.Id == id)).ConcurrencyStamp);
    }
}
