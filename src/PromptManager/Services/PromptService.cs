using Azure.Data.Tables;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PromptManager.Data;
using PromptManager.DTOs;
using PromptManager.Models;
using PromptManager.ViewModels;

namespace PromptManager.Services;

public class PromptService : IPromptService
{
    private readonly AppDbContext _db;
    private readonly TableClient? _tableClient;
    private readonly ILogger<PromptService> _logger;

    private const int MaxConcurrencyAttempts = 3;

    public PromptService(AppDbContext db, IConfiguration config, ILogger<PromptService> logger)
    {
        _db = db;
        _logger = logger;

        var connString = config["Azure:StorageConnectionString"];
        if (!string.IsNullOrEmpty(connString))
        {
            try
            {
                var serviceClient = new TableServiceClient(connString);
                serviceClient.CreateTableIfNotExists("PromptUsageHistory");
                _tableClient = serviceClient.GetTableClient("PromptUsageHistory");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not connect to Azure Table Storage. Usage history will not be persisted.");
            }
        }
    }

    public async Task<List<PromptListDto>> GetAllAsync(string? search, string? category, string? aiTool, string sortBy)
    {
        var query = _db.Prompts.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(p => p.Title.Contains(search) || p.Description.Contains(search) || p.CurrentText.Contains(search));

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(p => p.Category == category);

        if (!string.IsNullOrWhiteSpace(aiTool))
            query = query.Where(p => p.AiTool == aiTool);

        query = sortBy switch
        {
            "title" => query.OrderBy(p => p.Title),
            "date" => query.OrderByDescending(p => p.CreatedAt),
            "version" => query.OrderByDescending(p => p.CurrentVersion),
            _ => query.OrderByDescending(p => p.AverageRating)
        };

        return await query.Select(p => new PromptListDto
        {
            Id = p.Id,
            Title = p.Title,
            Category = p.Category,
            AiTool = p.AiTool,
            Author = p.Author,
            AverageRating = p.AverageRating,
            CurrentVersion = p.CurrentVersion,
            IsArchived = p.IsArchived
        }).ToListAsync();
    }

    public async Task<PromptDetailsDto?> GetByIdAsync(int id)
    {
        var prompt = await _db.Prompts
            .Include(p => p.Versions.OrderByDescending(v => v.VersionNumber))
            .FirstOrDefaultAsync(p => p.Id == id);

        if (prompt == null) return null;

        return new PromptDetailsDto
        {
            Id = prompt.Id,
            Title = prompt.Title,
            Description = prompt.Description,
            CurrentText = prompt.CurrentText,
            Category = prompt.Category,
            AiTool = prompt.AiTool,
            Author = prompt.Author,
            AverageRating = prompt.AverageRating,
            CurrentVersion = prompt.CurrentVersion,
            CreatedAt = prompt.CreatedAt,
            UpdatedAt = prompt.UpdatedAt,
            IsArchived = prompt.IsArchived,
            Versions = prompt.Versions.Select(v => new PromptVersionDto
            {
                Id = v.Id,
                VersionNumber = v.VersionNumber,
                PromptText = v.PromptText,
                ChangeNote = v.ChangeNote,
                CreatedBy = v.CreatedBy,
                CreatedAt = v.CreatedAt
            }).ToList()
        };
    }

    public async Task<int> CreateAsync(CreatePromptDto dto)
    {
        var prompt = new Prompt
        {
            Title = dto.Title,
            Description = dto.Description ?? string.Empty,
            CurrentText = dto.PromptText,
            Category = dto.Category,
            AiTool = dto.AiTool,
            Author = dto.Author,
            CurrentVersion = 1
        };

        // Adding the version through the navigation property lets EF insert both
        // rows in one SaveChanges, i.e. one transaction: no prompt without a version.
        prompt.Versions.Add(new PromptVersion
        {
            VersionNumber = 1,
            PromptText = dto.PromptText,
            ChangeNote = "Initial version",
            CreatedBy = dto.Author,
            CreatedAt = prompt.CreatedAt
        });

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
                p => Math.Round(p.Ratings.Average(r => (double)r.Rating), 2)));

        return (true, string.Empty);
    }

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

    public async Task<(bool success, string error)> UsePromptAsync(UsePromptDto dto)
    {
        var prompt = await _db.Prompts.FindAsync(dto.PromptId);
        if (prompt == null) return (false, "Prompt not found.");
        if (prompt.IsArchived) return (false, "Cannot use an archived prompt.");

        if (_tableClient != null)
        {
            var entity = new PromptUsageHistoryEntity
            {
                PartitionKey = prompt.Category,
                RowKey = Guid.NewGuid().ToString(),
                PromptId = dto.PromptId.ToString(),
                PromptTitle = prompt.Title,
                UserName = dto.UserName ?? string.Empty,
                AiTool = prompt.AiTool,
                Category = prompt.Category,
                UsedAt = DateTime.UtcNow
            };

            try
            {
                await _tableClient.AddEntityAsync(entity);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save usage history to Azure Table Storage.");
            }
        }

        return (true, string.Empty);
    }

    public async Task<List<UsageHistoryDto>> GetUsageHistoryAsync()
    {
        if (_tableClient == null)
            return new List<UsageHistoryDto>();

        var results = new List<UsageHistoryDto>();

        await foreach (var entity in _tableClient.QueryAsync<PromptUsageHistoryEntity>())
        {
            results.Add(new UsageHistoryDto
            {
                PromptId = entity.PromptId,
                PromptTitle = entity.PromptTitle,
                UserName = entity.UserName,
                AiTool = entity.AiTool,
                Category = entity.Category,
                UsedAt = entity.UsedAt
            });
        }

        return results.OrderByDescending(u => u.UsedAt).ToList();
    }

    public async Task<(int totalPrompts, int totalUsages)> GetHealthStatsAsync()
    {
        var totalPrompts = await _db.Prompts.CountAsync();
        var totalUsages = 0;

        if (_tableClient != null)
        {
            try
            {
                await foreach (var _ in _tableClient.QueryAsync<PromptUsageHistoryEntity>(select: new[] { "RowKey" }))
                    totalUsages++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to count usage history.");
            }
        }

        return (totalPrompts, totalUsages);
    }

    // SQLITE_CONSTRAINT_UNIQUE. Provider-specific on purpose: if the app moves to
    // another database, this is the one place to update.
    private static bool IsUniqueViolation(Exception ex) =>
        ex is DbUpdateException { InnerException: SqliteException { SqliteExtendedErrorCode: 2067 } };
}
