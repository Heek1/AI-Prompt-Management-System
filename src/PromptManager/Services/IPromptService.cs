using PromptManager.DTOs;
using PromptManager.ViewModels;

namespace PromptManager.Services;

public interface IPromptService
{
    /// <summary>All matching prompts, archived included, unpaged.</summary>
    Task<List<PromptListDto>> GetAllAsync(string? search, string? category, string? aiTool, string sortBy, string? tag = null);

    /// <summary>One page of matching prompts; archived ones only when asked for.</summary>
    Task<PromptPageDto> GetPageAsync(string? search, string? category, string? aiTool, string sortBy,
        bool includeArchived, int page, int pageSize, string? tag = null);

    /// <summary>Tags in use with how many prompts carry each, most used first.</summary>
    Task<List<TagCountDto>> TagCountsAsync(bool includeArchived);

    /// <summary>Updates title, description, category, tool and tags. False when the prompt does not exist.</summary>
    Task<bool> UpdateAsync(EditPromptDto dto);
    Task<bool> UnarchiveAsync(int id);

    /// <summary>Deletes the prompt with its versions, ratings, runs and input sets.</summary>
    Task<bool> DeleteAsync(int id);
    Task<PromptDetailsDto?> GetByIdAsync(int id);
    Task<int> CreateAsync(CreatePromptDto dto);
    Task<bool> CreateVersionAsync(CreatePromptVersionDto dto);
    Task<(bool success, string error)> RateAsync(RatePromptDto dto);
    Task<bool> ArchiveAsync(int id);
}
