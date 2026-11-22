using PromptManager.DTOs;
using PromptManager.ViewModels;

namespace PromptManager.Services;

public interface IPromptService
{
    /// <summary>All matching prompts, archived included, unpaged.</summary>
    Task<List<PromptListDto>> GetAllAsync(string? search, string? category, string? aiTool, string sortBy);

    /// <summary>One page of matching prompts; archived ones only when asked for.</summary>
    Task<PromptPageDto> GetPageAsync(string? search, string? category, string? aiTool, string sortBy,
        bool includeArchived, int page, int pageSize);

    /// <summary>Updates title, description, category and tool. False when the prompt does not exist.</summary>
    Task<bool> UpdateAsync(EditPromptDto dto);
    Task<bool> UnarchiveAsync(int id);

    /// <summary>Deletes the prompt with its versions, ratings, runs and input sets.</summary>
    Task<bool> DeleteAsync(int id);
    Task<PromptDetailsDto?> GetByIdAsync(int id);
    Task<int> CreateAsync(CreatePromptDto dto);
    Task<bool> CreateVersionAsync(CreatePromptVersionDto dto);
    Task<(bool success, string error)> RateAsync(RatePromptDto dto);
    Task<bool> ArchiveAsync(int id);
    Task<(bool success, string error)> UsePromptAsync(UsePromptDto dto);
    Task<List<UsageHistoryDto>> GetUsageHistoryAsync();
    Task<(int totalPrompts, int totalUsages)> GetHealthStatsAsync();
}
