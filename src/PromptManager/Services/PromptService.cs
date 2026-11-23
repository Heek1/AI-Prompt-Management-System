using Npgsql;
using Microsoft.EntityFrameworkCore;
using PromptManager.Data;
using PromptManager.DTOs;
using PromptManager.Infrastructure;
using PromptManager.Models;
using PromptManager.ViewModels;

namespace PromptManager.Services;

public class PromptService : IPromptService
{
    private readonly AppDbContext _db;
    private readonly ILogger<PromptService> _logger;

    private const int MaxConcurrencyAttempts = 3;

    public PromptService(AppDbContext db, ILogger<PromptService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task<List<PromptListDto>> GetAllAsync(string? search, string? category, string? aiTool, string sortBy, string? tag = null) =>
        Project(Filter(search, category, aiTool, sortBy, includeArchived: true, tag)).ToListAsync();

    public async Task<PromptPageDto> GetPageAsync(string? search, string? category, string? aiTool, string sortBy,
        bool includeArchived, int page, int pageSize, string? tag = null)
    {
        page = Math.Max(1, page);
        var query = Filter(search, category, aiTool, sortBy, includeArchived, tag);
        return new PromptPageDto
        {
            TotalCount = await query.CountAsync(),
            Prompts = await Project(query.Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(),
            Page = page,
            PageSize = pageSize
        };
    }

    public Task<List<TagCountDto>> TagCountsAsync(bool includeArchived) =>
        _db.Tags.AsNoTracking()
            .Select(t => new
            {
                t.Name,
                t.Slug,
                Count = t.PromptTags.Count(pt => includeArchived || !pt.Prompt!.IsArchived)
            })
            .Where(t => t.Count > 0)
            .OrderByDescending(t => t.Count).ThenBy(t => t.Name)
            .Select(t => new TagCountDto(t.Name, t.Slug, t.Count))
            .ToListAsync();

    private IQueryable<Prompt> Filter(string? search, string? category, string? aiTool, string sortBy, bool includeArchived,
        string? tag = null)
    {
        var query = _db.Prompts.AsNoTracking();

        if (!includeArchived)
            query = query.Where(p => !p.IsArchived);

        if (!string.IsNullOrWhiteSpace(search))
        {
            // ILIKE keeps search case-insensitive, as it was on SQLite. % and _
            // in the input are escaped so they match literally.
            var pattern = "%" + search.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
            // Npgsql emits ESCAPE '' unless an escape character is given.
            query = query.Where(p => EF.Functions.ILike(p.Title, pattern, @"\")
                || EF.Functions.ILike(p.Description, pattern, @"\")
                || EF.Functions.ILike(p.CurrentText, pattern, @"\"));
        }

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(p => p.Category == category);

        if (!string.IsNullOrWhiteSpace(aiTool))
            query = query.Where(p => p.AiTool == aiTool);

        if (!string.IsNullOrWhiteSpace(tag))
            query = query.Where(p => p.PromptTags.Any(pt => pt.Tag!.Slug == tag));

        // Id as the last key keeps paging stable when the main key ties.
        return sortBy switch
        {
            "title" => query.OrderBy(p => p.Title).ThenBy(p => p.Id),
            "date" => query.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id),
            "version" => query.OrderByDescending(p => p.CurrentVersion).ThenByDescending(p => p.Id),
            _ => query.OrderByDescending(p => p.AverageRating).ThenByDescending(p => p.Id)
        };
    }

    private static IQueryable<PromptListDto> Project(IQueryable<Prompt> query) =>
        query.Select(p => new PromptListDto
        {
            Id = p.Id,
            Title = p.Title,
            Category = p.Category,
            AiTool = p.AiTool,
            Author = p.Author,
            AverageRating = p.AverageRating,
            CurrentVersion = p.CurrentVersion,
            IsArchived = p.IsArchived,
            OwnerId = p.OwnerId,
            ProductionVersion = p.ProductionVersion,
            UpdatedAt = p.UpdatedAt,
            Excerpt = p.CurrentText.Length > 160 ? p.CurrentText.Substring(0, 160) : p.CurrentText,
            Tags = p.PromptTags.OrderBy(pt => pt.Tag!.Name).Select(pt => new TagDto(pt.Tag!.Name, pt.Tag.Slug)).ToList()
        });

    public async Task<PromptDetailsDto?> GetByIdAsync(int id)
    {
        // No tracking: the result is mapped to a DTO, and tracked tag links
        // would clash with the ones UpdateAsync adds in the same request.
        var prompt = await _db.Prompts
            .AsNoTracking()
            .Include(p => p.Versions.OrderByDescending(v => v.VersionNumber))
            .Include(p => p.PromptTags).ThenInclude(pt => pt.Tag)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == id);

        if (prompt == null) return null;

        return new PromptDetailsDto
        {
            Id = prompt.Id,
            Title = prompt.Title,
            Slug = prompt.Slug,
            Description = prompt.Description,
            CurrentText = prompt.CurrentText,
            Category = prompt.Category,
            AiTool = prompt.AiTool,
            Author = prompt.Author,
            OwnerId = prompt.OwnerId,
            ProductionVersion = prompt.ProductionVersion,
            AverageRating = prompt.AverageRating,
            CurrentVersion = prompt.CurrentVersion,
            CreatedAt = prompt.CreatedAt,
            UpdatedAt = prompt.UpdatedAt,
            IsArchived = prompt.IsArchived,
            Tags = prompt.PromptTags.Select(pt => new TagDto(pt.Tag!.Name, pt.Tag.Slug)).OrderBy(t => t.Name).ToList(),
            Versions = prompt.Versions.Select(v => new PromptVersionDto
            {
                Id = v.Id,
                VersionNumber = v.VersionNumber,
                PromptText = v.PromptText,
                ChangeNote = v.ChangeNote,
                CreatedBy = v.CreatedBy,
                CreatedAt = v.CreatedAt,
                Status = v.Status,
                PromotedAt = v.PromotedAt,
                PromotedBy = v.PromotedBy,
                PromotedOverRegression = v.PromotedOverRegression
            }).ToList()
        };
    }

    public async Task<int> CreateAsync(CreatePromptDto dto)
    {
        var slug = await Slugs.UniqueAsync(dto.Title, s => _db.Prompts.AnyAsync(p => p.Slug == s));
        var prompt = new Prompt
        {
            Title = dto.Title,
            Slug = slug,
            Description = dto.Description ?? string.Empty,
            CurrentText = dto.PromptText,
            Category = dto.Category,
            AiTool = dto.AiTool,
            Author = dto.Author,
            OwnerId = dto.OwnerId,
            CurrentVersion = 1,
            ProductionVersion = 1
        };

        // Adding the version through the navigation property lets EF insert both
        // rows in one SaveChanges, i.e. one transaction: no prompt without a version.
        prompt.Versions.Add(new PromptVersion
        {
            VersionNumber = 1,
            PromptText = dto.PromptText,
            ChangeNote = "Initial version",
            CreatedBy = dto.Author,
            CreatedById = dto.OwnerId,
            // The first version has nothing to regress against, so it goes
            // straight to production; later versions start as drafts.
            Status = VersionStatus.Production,
            PromotedAt = prompt.CreatedAt,
            PromotedBy = dto.Author,
            CreatedAt = prompt.CreatedAt
        });

        foreach (var tag in await ResolveTagsAsync(dto.Tags))
            prompt.PromptTags.Add(new PromptTag { Tag = tag });

        _db.Prompts.Add(prompt);
        await _db.SaveChangesAsync();

        return prompt.Id;
    }

    public async Task<bool> CreateVersionAsync(CreatePromptVersionDto dto)
    {
        for (var attempt = 1; attempt <= MaxConcurrencyAttempts; attempt++)
        {
            var prompt = await _db.Prompts.FirstOrDefaultAsync(p => p.Id == dto.PromptId);
            if (prompt == null || prompt.IsArchived) return false;

            var now = DateTime.UtcNow;
            prompt.CurrentVersion++;
            prompt.CurrentText = dto.PromptText;
            prompt.UpdatedAt = now;
            prompt.ConcurrencyStamp = Guid.NewGuid().ToString();

            _db.PromptVersions.Add(new PromptVersion
            {
                PromptId = dto.PromptId,
                VersionNumber = prompt.CurrentVersion,
                PromptText = dto.PromptText,
                ChangeNote = dto.ChangeNote ?? string.Empty,
                CreatedBy = dto.CreatedBy,
                CreatedById = dto.CreatedById,
                CreatedAt = now
            });

            try
            {
                await _db.SaveChangesAsync();
                return true;
            }
            catch (Exception ex) when (ex is DbUpdateConcurrencyException || IsUniqueViolation(ex))
            {
                // Someone else changed the prompt (or took this version number)
                // between our read and write. Re-read and try again.
                _logger.LogInformation("Version conflict on prompt {PromptId}, attempt {Attempt}.", dto.PromptId, attempt);
                _db.ChangeTracker.Clear();
            }
        }

        _logger.LogWarning("Gave up creating a version for prompt {PromptId} after {Attempts} conflicts.", dto.PromptId, MaxConcurrencyAttempts);
        return false;
    }

    public async Task<(bool success, string error)> RateAsync(RatePromptDto dto)
    {
        if (!await _db.Prompts.AnyAsync(p => p.Id == dto.PromptId))
            return (false, "Prompt not found.");

        _db.PromptRatings.Add(new PromptRating
        {
            PromptId = dto.PromptId,
            UserName = dto.UserName,
            UserId = dto.UserId,
            Rating = dto.Rating,
            Comment = dto.Comment ?? string.Empty
        });

        // No "already rated?" query first: two requests could both pass it.
        // The unique index on (PromptId, UserName) decides instead.
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            _db.ChangeTracker.Clear();
            return (false, "You have already rated this prompt.");
        }

        // Recomputed in a single UPDATE from the ratings table, so concurrent
        // ratings cannot overwrite each other's average.
        await _db.Prompts
            .Where(p => p.Id == dto.PromptId)
            .ExecuteUpdateAsync(s => s.SetProperty(
                p => p.AverageRating,
                // PostgreSQL only has round(numeric, int), so average as decimal.
                p => (double)Math.Round(p.Ratings.Average(r => (decimal)r.Rating), 2)));

        return (true, string.Empty);
    }

    public async Task<bool> UpdateAsync(EditPromptDto dto)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        var updated = await _db.Prompts
            .Where(p => p.Id == dto.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Title, dto.Title)
                .SetProperty(p => p.Description, dto.Description ?? string.Empty)
                .SetProperty(p => p.Category, dto.Category)
                .SetProperty(p => p.AiTool, dto.AiTool)
                .SetProperty(p => p.UpdatedAt, DateTime.UtcNow)
                .SetProperty(p => p.ConcurrencyStamp, Guid.NewGuid().ToString())) > 0;
        if (!updated) return false;

        // Replace the tag set: dropping all links and adding the new ones is
        // simpler than a diff and the set is at most a few rows.
        var tags = await ResolveTagsAsync(dto.Tags);
        await _db.PromptTags.Where(pt => pt.PromptId == dto.Id).ExecuteDeleteAsync();
        _db.PromptTags.AddRange(tags.Select(t => new PromptTag { PromptId = dto.Id, Tag = t }));
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return true;
    }

    /// <summary>
    /// The tags named in the form, existing ones reused by slug and new ones
    /// created (but not saved) here. Invalid input was rejected by the
    /// controller; anything left over is cut to the limit.
    /// </summary>
    private async Task<List<Tag>> ResolveTagsAsync(string? input)
    {
        var parsed = TagNames.Parse(input).Tags.Take(TagNames.MaxTags).ToList();
        if (parsed.Count == 0) return [];

        var slugs = parsed.Select(t => t.Slug).ToList();
        var existing = await _db.Tags.Where(t => slugs.Contains(t.Slug)).ToDictionaryAsync(t => t.Slug);
        return parsed.Select(t => existing.TryGetValue(t.Slug, out var tag) ? tag : new Tag { Name = t.Name, Slug = t.Slug }).ToList();
    }

    public async Task<bool> UnarchiveAsync(int id) =>
        await _db.Prompts
            .Where(p => p.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.IsArchived, false)
                .SetProperty(p => p.UpdatedAt, DateTime.UtcNow)
                .SetProperty(p => p.ConcurrencyStamp, Guid.NewGuid().ToString())) > 0;

    // Versions, ratings, runs and input sets go with it through ON DELETE CASCADE.
    public async Task<bool> DeleteAsync(int id) =>
        await _db.Prompts.Where(p => p.Id == id).ExecuteDeleteAsync() > 0;

    public async Task<bool> ArchiveAsync(int id)
    {
        // Unconditional single UPDATE: archiving is idempotent, and the new stamp
        // makes a concurrent CreateVersionAsync retry and see the archived flag.
        var updated = await _db.Prompts
            .Where(p => p.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.IsArchived, true)
                .SetProperty(p => p.UpdatedAt, DateTime.UtcNow)
                .SetProperty(p => p.ConcurrencyStamp, Guid.NewGuid().ToString()));

        return updated > 0;
    }

    // PostgreSQL unique_violation. Provider-specific on purpose: if the app moves
    // to another database, this is the one place to update.
    private static bool IsUniqueViolation(Exception ex) =>
        ex is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } };
}
