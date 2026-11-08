using Azure.Data.Tables;
using Microsoft.EntityFrameworkCore;
using AI_Prompt_Management_System.Data;
using AI_Prompt_Management_System.DTOs;
using AI_Prompt_Management_System.Models;
using AI_Prompt_Management_System.ViewModels;

namespace AI_Prompt_Management_System.Services;

public class PromptService : IPromptService
{
    private readonly AppDbContext _db;
    private readonly TableClient? _tableClient;
    private readonly ILogger<PromptService> _logger;

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
            Description = dto.Description,
            CurrentText = dto.PromptText,
            Category = dto.Category,
            AiTool = dto.AiTool,
            Author = dto.Author,
            CurrentVersion = 1
        };

        _db.Prompts.Add(prompt);
        await _db.SaveChangesAsync();

        // Create initial version
        var version = new PromptVersion
        {
            PromptId = prompt.Id,
            VersionNumber = 1,
            PromptText = dto.PromptText,
            ChangeNote = "Initial version",
            CreatedBy = dto.Author,
            CreatedAt = DateTime.UtcNow
        };
        _db.PromptVersions.Add(version);
        await _db.SaveChangesAsync();

        return prompt.Id;
    }

    public async Task<bool> CreateVersionAsync(CreatePromptVersionDto dto)
    {
        var prompt = await _db.Prompts.FindAsync(dto.PromptId);
        if (prompt == null || prompt.IsArchived) return false;

        prompt.CurrentVersion++;
        prompt.CurrentText = dto.PromptText;
        prompt.UpdatedAt = DateTime.UtcNow;

        var version = new PromptVersion
        {
            PromptId = dto.PromptId,
            VersionNumber = prompt.CurrentVersion,
            PromptText = dto.PromptText,
            ChangeNote = dto.ChangeNote,
            CreatedBy = dto.CreatedBy,
            CreatedAt = DateTime.UtcNow
        };

        _db.PromptVersions.Add(version);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<(bool success, string error)> RateAsync(RatePromptDto dto)
    {
        var prompt = await _db.Prompts.FindAsync(dto.PromptId);
        if (prompt == null) return (false, "Prompt not found.");

        var existing = await _db.PromptRatings
            .FirstOrDefaultAsync(r => r.PromptId == dto.PromptId && r.UserName == dto.UserName);

        if (existing != null)
            return (false, "You have already rated this prompt.");

        var rating = new PromptRating
        {
            PromptId = dto.PromptId,
            UserName = dto.UserName,
            Rating = dto.Rating,
            Comment = dto.Comment
        };

        _db.PromptRatings.Add(rating);
        await _db.SaveChangesAsync();

        // Recalculate average
        var avg = await _db.PromptRatings
            .Where(r => r.PromptId == dto.PromptId)
            .AverageAsync(r => (double)r.Rating);

        prompt.AverageRating = Math.Round(avg, 2);
        await _db.SaveChangesAsync();

        return (true, string.Empty);
    }

    public async Task<bool> ArchiveAsync(int id)
    {
        var prompt = await _db.Prompts.FindAsync(id);
        if (prompt == null) return false;

        prompt.IsArchived = true;
        prompt.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
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
                UserName = dto.UserName,
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
}
