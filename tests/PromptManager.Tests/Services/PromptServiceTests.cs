using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PromptManager.Data;
using PromptManager.DTOs;
using PromptManager.Services;
using PromptManager.Tests.Infrastructure;

namespace PromptManager.Tests.Services;

public sealed class PromptServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    // A fresh context per call mirrors the scoped lifetime in the app, so tests
    // read what was persisted rather than what is cached in a change tracker.
    private PromptService CreateService(AppDbContext? db = null) =>
        new(db ?? _database.CreateContext(),
            new ConfigurationBuilder().Build(),
            NullLogger<PromptService>.Instance);

    private static CreatePromptDto NewPrompt(
        string title = "Review a diff",
        string text = "Review this diff and list correctness bugs first.",
        string category = "Code Generation",
        string aiTool = "Claude",
        string description = "",
        string author = "alice") => new()
        {
            Title = title,
            PromptText = text,
            Category = category,
            AiTool = aiTool,
            Description = description,
            Author = author
        };

    private Task<int> CreatePromptAsync(CreatePromptDto? dto = null) =>
        CreateService().CreateAsync(dto ?? NewPrompt());

    // ---------- Create / GetById ----------

    [Fact]
    public async Task CreateAsync_stores_prompt_with_initial_version()
    {
        var id = await CreatePromptAsync();

        var details = await CreateService().GetByIdAsync(id);

        Assert.NotNull(details);
        Assert.Equal("Review a diff", details.Title);
        Assert.Equal(1, details.CurrentVersion);
        var version = Assert.Single(details.Versions);
        Assert.Equal(1, version.VersionNumber);
        Assert.Equal("Initial version", version.ChangeNote);
        Assert.Equal("alice", version.CreatedBy);
        Assert.Equal(details.CurrentText, version.PromptText);
    }

    [Fact]
    public async Task GetByIdAsync_returns_null_for_unknown_id()
    {
        Assert.Null(await CreateService().GetByIdAsync(12345));
    }

    [Fact]
    public async Task GetByIdAsync_orders_versions_newest_first()
    {
        var id = await CreatePromptAsync();
        await CreateService().CreateVersionAsync(new CreatePromptVersionDto
        {
            PromptId = id, PromptText = "Second text that is long enough.", CreatedBy = "bob"
        });

        var details = await CreateService().GetByIdAsync(id);

        Assert.Equal(new[] { 2, 1 }, details!.Versions.Select(v => v.VersionNumber));
    }

    // ---------- Versions ----------

    [Fact]
    public async Task CreateVersionAsync_increments_version_and_updates_current_text()
    {
        var id = await CreatePromptAsync();

        var ok = await CreateService().CreateVersionAsync(new CreatePromptVersionDto
        {
            PromptId = id,
            PromptText = "A clearer second version of the prompt.",
            ChangeNote = "Clarified",
            CreatedBy = "bob"
        });

        Assert.True(ok);
        var details = await CreateService().GetByIdAsync(id);
        Assert.Equal(2, details!.CurrentVersion);
        Assert.Equal("A clearer second version of the prompt.", details.CurrentText);
        Assert.Equal("Clarified", details.Versions[0].ChangeNote);
    }

    [Fact]
    public async Task CreateVersionAsync_returns_false_for_unknown_prompt()
    {
        var ok = await CreateService().CreateVersionAsync(new CreatePromptVersionDto
        {
            PromptId = 999, PromptText = "Does not matter at all here.", CreatedBy = "bob"
        });

        Assert.False(ok);
    }

    [Fact]
    public async Task CreateVersionAsync_returns_false_for_archived_prompt()
    {
        var id = await CreatePromptAsync();
        await CreateService().ArchiveAsync(id);

        var ok = await CreateService().CreateVersionAsync(new CreatePromptVersionDto
        {
            PromptId = id, PromptText = "Trying to edit an archived one.", CreatedBy = "bob"
        });

        Assert.False(ok);
        Assert.Equal(1, (await CreateService().GetByIdAsync(id))!.CurrentVersion);
    }

    // ---------- Ratings ----------

    [Fact]
    public async Task RateAsync_stores_rating_and_updates_average()
    {
        var id = await CreatePromptAsync();

        var (success, error) = await CreateService().RateAsync(new RatePromptDto { PromptId = id, UserName = "bob", UserId = "bob", Rating = 4 });

        Assert.True(success);
        Assert.Equal(string.Empty, error);
        Assert.Equal(4, (await CreateService().GetByIdAsync(id))!.AverageRating);
    }

    [Fact]
    public async Task RateAsync_averages_ratings_from_different_users_to_two_decimals()
    {
        var id = await CreatePromptAsync();
        await CreateService().RateAsync(new RatePromptDto { PromptId = id, UserName = "a", UserId = "a", Rating = 5 });
        await CreateService().RateAsync(new RatePromptDto { PromptId = id, UserName = "b", UserId = "b", Rating = 4 });
        await CreateService().RateAsync(new RatePromptDto { PromptId = id, UserName = "c", UserId = "c", Rating = 4 });

        Assert.Equal(4.33, (await CreateService().GetByIdAsync(id))!.AverageRating);
    }

    [Fact]
    public async Task RateAsync_rejects_second_rating_from_same_user()
    {
        var id = await CreatePromptAsync();
        await CreateService().RateAsync(new RatePromptDto { PromptId = id, UserName = "bob", UserId = "bob", Rating = 5 });

        var (success, error) = await CreateService().RateAsync(new RatePromptDto { PromptId = id, UserName = "bob", UserId = "bob", Rating = 1 });

        Assert.False(success);
        Assert.Equal("You have already rated this prompt.", error);
        Assert.Equal(5, (await CreateService().GetByIdAsync(id))!.AverageRating);
    }

    [Fact]
    public async Task RateAsync_fails_for_unknown_prompt()
    {
        var (success, error) = await CreateService().RateAsync(new RatePromptDto { PromptId = 999, UserName = "bob", UserId = "bob", Rating = 3 });

        Assert.False(success);
        Assert.Equal("Prompt not found.", error);
    }

    // ---------- Archive ----------

    [Fact]
    public async Task ArchiveAsync_marks_prompt_archived()
    {
        var id = await CreatePromptAsync();

        Assert.True(await CreateService().ArchiveAsync(id));
        Assert.True((await CreateService().GetByIdAsync(id))!.IsArchived);
    }

    [Fact]
    public async Task ArchiveAsync_returns_false_for_unknown_prompt()
    {
        Assert.False(await CreateService().ArchiveAsync(999));
    }

    // ---------- Search, filter, sort ----------

    [Theory]
    [InlineData("diff")]          // title
    [InlineData("checklist")]     // description
    [InlineData("correctness")]   // prompt text
    public async Task GetAllAsync_searches_title_description_and_text(string term)
    {
        await CreatePromptAsync(NewPrompt(title: "Review a diff", description: "A checklist", text: "List correctness bugs in this change."));
        await CreatePromptAsync(NewPrompt(title: "Unrelated", text: "Write a poem about the sea, please."));

        var result = await CreateService().GetAllAsync(term, null, null, "rating");

        Assert.Equal("Review a diff", Assert.Single(result).Title);
    }

    [Theory]
    [InlineData("REVIEW", "Review a diff")]   // case-insensitive
    [InlineData("100%", "Coverage 100% please")] // % is literal
    [InlineData("a_b", "Name a_b exactly")]   // _ is literal
    public async Task GetAllAsync_search_is_case_insensitive_and_literal(string term, string expected)
    {
        const string text = "Neutral prompt text without the search terms.";
        await CreatePromptAsync(NewPrompt(title: "Review a diff", text: text));
        await CreatePromptAsync(NewPrompt(title: "Coverage 100% please", text: text));
        await CreatePromptAsync(NewPrompt(title: "Coverage 1000 please", text: text));
        await CreatePromptAsync(NewPrompt(title: "Name a_b exactly", text: text));
        await CreatePromptAsync(NewPrompt(title: "Name axb exactly", text: text));

        var result = await CreateService().GetAllAsync(term, null, null, "title");

        Assert.Equal(expected, Assert.Single(result).Title);
    }

    [Fact]
    public async Task GetAllAsync_filters_by_category_and_tool()
    {
        await CreatePromptAsync(NewPrompt(title: "A", category: "Testing", aiTool: "Claude"));
        await CreatePromptAsync(NewPrompt(title: "B", category: "Testing", aiTool: "Gemini"));
        await CreatePromptAsync(NewPrompt(title: "C", category: "Learning", aiTool: "Claude"));

        var byCategory = await CreateService().GetAllAsync(null, "Testing", null, "title");
        var byBoth = await CreateService().GetAllAsync(null, "Testing", "Gemini", "title");

        Assert.Equal(new[] { "A", "B" }, byCategory.Select(p => p.Title));
        Assert.Equal("B", Assert.Single(byBoth).Title);
    }

    [Fact]
    public async Task GetAllAsync_sorts_by_title_version_and_rating()
    {
        var b = await CreatePromptAsync(NewPrompt(title: "Bravo"));
        var a = await CreatePromptAsync(NewPrompt(title: "Alpha"));
        await CreateService().CreateVersionAsync(new CreatePromptVersionDto { PromptId = b, PromptText = "Second version of Bravo text.", CreatedBy = "x" });
        await CreateService().RateAsync(new RatePromptDto { PromptId = a, UserName = "u", UserId = "u", Rating = 5 });

        var service = CreateService();
        Assert.Equal(new[] { "Alpha", "Bravo" }, (await service.GetAllAsync(null, null, null, "title")).Select(p => p.Title));
        Assert.Equal(new[] { "Bravo", "Alpha" }, (await service.GetAllAsync(null, null, null, "version")).Select(p => p.Title));
        Assert.Equal(new[] { "Alpha", "Bravo" }, (await service.GetAllAsync(null, null, null, "rating")).Select(p => p.Title));
    }

    [Fact]
    public async Task GetAllAsync_sorts_by_date_newest_first()
    {
        await CreatePromptAsync(NewPrompt(title: "Old"));
        await CreatePromptAsync(NewPrompt(title: "New"));
        using (var db = _database.CreateContext())
        {
            var old = await db.Prompts.SingleAsync(p => p.Title == "Old");
            old.CreatedAt = DateTime.UtcNow.AddDays(-10);
            await db.SaveChangesAsync();
        }

        var result = await CreateService().GetAllAsync(null, null, null, "date");

        Assert.Equal(new[] { "New", "Old" }, result.Select(p => p.Title));
    }

    // ---------- Usage history without Azure ----------

    [Fact]
    public async Task UsePromptAsync_succeeds_without_azure_storage()
    {
        var id = await CreatePromptAsync();

        var (success, _) = await CreateService().UsePromptAsync(new UsePromptDto { PromptId = id, UserName = "bob" });

        Assert.True(success);
        Assert.Empty(await CreateService().GetUsageHistoryAsync());
    }

    [Fact]
    public async Task UsePromptAsync_rejects_archived_and_unknown_prompts()
    {
        var id = await CreatePromptAsync();
        await CreateService().ArchiveAsync(id);

        var archived = await CreateService().UsePromptAsync(new UsePromptDto { PromptId = id });
        var unknown = await CreateService().UsePromptAsync(new UsePromptDto { PromptId = 999 });

        Assert.Equal((false, "Cannot use an archived prompt."), archived);
        Assert.Equal((false, "Prompt not found."), unknown);
    }

    [Fact]
    public async Task GetHealthStatsAsync_counts_prompts()
    {
        await CreatePromptAsync();
        await CreatePromptAsync(NewPrompt(title: "Second"));

        Assert.Equal((2, 0), await CreateService().GetHealthStatsAsync());
    }

    // ---------- Paging, edit, restore, delete ----------

    [Fact]
    public async Task GetPageAsync_pages_and_hides_archived_by_default()
    {
        for (var i = 1; i <= 5; i++)
            await CreatePromptAsync(NewPrompt(title: $"P{i}"));
        var archived = await CreatePromptAsync(NewPrompt(title: "Old"));
        await CreateService().ArchiveAsync(archived);

        var first = await CreateService().GetPageAsync(null, null, null, "title", includeArchived: false, page: 1, pageSize: 2);
        var last = await CreateService().GetPageAsync(null, null, null, "title", includeArchived: false, page: 3, pageSize: 2);
        var withArchived = await CreateService().GetPageAsync(null, null, null, "title", includeArchived: true, page: 1, pageSize: 10);

        Assert.Equal((5, 3), (first.TotalCount, first.PageCount));
        Assert.Equal(new[] { "P1", "P2" }, first.Prompts.Select(p => p.Title));
        Assert.Equal("P5", Assert.Single(last.Prompts).Title);
        Assert.Equal(6, withArchived.TotalCount);
    }

    [Fact]
    public async Task UpdateAsync_changes_metadata_but_not_text_or_version()
    {
        var id = await CreatePromptAsync();

        var ok = await CreateService().UpdateAsync(new EditPromptDto
        {
            Id = id, Title = "New title", Description = null, Category = "Testing", AiTool = "Gemini"
        });

        Assert.True(ok);
        var details = await CreateService().GetByIdAsync(id);
        Assert.Equal(("New title", "", "Testing", "Gemini"), (details!.Title, details.Description, details.Category, details.AiTool));
        Assert.Equal(1, details.CurrentVersion);
        Assert.False(await CreateService().UpdateAsync(new EditPromptDto { Id = 999, Title = "x", Category = "Testing", AiTool = "Claude" }));
    }

    [Fact]
    public async Task UnarchiveAsync_restores_an_archived_prompt()
    {
        var id = await CreatePromptAsync();
        await CreateService().ArchiveAsync(id);

        Assert.True(await CreateService().UnarchiveAsync(id));
        Assert.False((await CreateService().GetByIdAsync(id))!.IsArchived);
    }

    [Fact]
    public async Task DeleteAsync_removes_the_prompt_with_everything_attached()
    {
        var id = await CreatePromptAsync();
        var keep = await CreatePromptAsync(NewPrompt(title: "Keep"));
        await CreateService().RateAsync(new RatePromptDto { PromptId = id, UserName = "u", UserId = "u", Rating = 3 });
        await CreateService().CreateVersionAsync(new CreatePromptVersionDto { PromptId = id, PromptText = "A second version that is long.", CreatedBy = "x" });

        Assert.True(await CreateService().DeleteAsync(id));
        Assert.False(await CreateService().DeleteAsync(id));

        using var db = _database.CreateContext();
        Assert.Equal(keep, (await db.Prompts.SingleAsync()).Id);
        Assert.Equal(1, await db.PromptVersions.CountAsync());
        Assert.Equal(0, await db.PromptRatings.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_records_the_owner_on_prompt_and_first_version()
    {
        var dto = NewPrompt();
        dto.OwnerId = "user-1";

        var id = await CreatePromptAsync(dto);

        using var db = _database.CreateContext();
        Assert.Equal("user-1", (await db.Prompts.SingleAsync(p => p.Id == id)).OwnerId);
        Assert.Equal("user-1", (await db.PromptVersions.SingleAsync(v => v.PromptId == id)).CreatedById);
    }
}
