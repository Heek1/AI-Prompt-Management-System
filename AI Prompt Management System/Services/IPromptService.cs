using AI_Prompt_Management_System.DTOs;
using AI_Prompt_Management_System.ViewModels;

namespace AI_Prompt_Management_System.Services;

public interface IPromptService
{
    Task<List<PromptListDto>> GetAllAsync(string? search, string? category, string? aiTool, string sortBy);
    Task<PromptDetailsDto?> GetByIdAsync(int id);
    Task<int> CreateAsync(CreatePromptDto dto);
    Task<bool> CreateVersionAsync(CreatePromptVersionDto dto);
    Task<(bool success, string error)> RateAsync(RatePromptDto dto);
    Task<bool> ArchiveAsync(int id);
    Task<(bool success, string error)> UsePromptAsync(UsePromptDto dto);
    Task<List<UsageHistoryDto>> GetUsageHistoryAsync();
    Task<(int totalPrompts, int totalUsages)> GetHealthStatsAsync();
}
