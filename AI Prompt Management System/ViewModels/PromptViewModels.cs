using AI_Prompt_Management_System.DTOs;

namespace AI_Prompt_Management_System.ViewModels;

public class PromptDetailsViewModel
{
    public PromptDetailsDto Prompt { get; set; } = new();
    public List<PromptVersionDto> Versions { get; set; } = new();
    public RatePromptDto RateForm { get; set; } = new();
    public CreatePromptVersionDto NewVersionForm { get; set; } = new();
}

public class PromptIndexViewModel
{
    public List<PromptListDto> Prompts { get; set; } = new();
    public string? SearchQuery { get; set; }
    public string? CategoryFilter { get; set; }
    public string? AiToolFilter { get; set; }
    public string SortBy { get; set; } = "rating";
    public List<string> Categories { get; set; } = new();
    public List<string> AiTools { get; set; } = new();
}

public class HistoryIndexViewModel
{
    public List<UsageHistoryDto> Usages { get; set; } = new();
    public int TotalCount { get; set; }
}

public class UsageHistoryDto
{
    public string PromptId { get; set; } = string.Empty;
    public string PromptTitle { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string AiTool { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public DateTime UsedAt { get; set; }
}
