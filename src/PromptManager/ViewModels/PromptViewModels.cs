using PromptManager.DTOs;
using PromptManager.Infrastructure;
using PromptManager.Llm;

namespace PromptManager.ViewModels;

public class PromptDetailsViewModel
{
    public PromptDetailsDto Prompt { get; set; } = new();
    public List<PromptVersionDto> Versions { get; set; } = new();
    public RatePromptDto RateForm { get; set; } = new();
    public CreatePromptVersionDto NewVersionForm { get; set; } = new();
    public RunPromptDto RunForm { get; set; } = new();
    public IReadOnlyList<AvailableModel> Models { get; set; } = [];
    public List<RunListItemDto> RecentRuns { get; set; } = [];

    /// <summary>Variables used by any version, those of the current version first.</summary>
    public IReadOnlyList<string> Variables { get; set; } = [];
    public List<InputSetDto> InputSets { get; set; } = [];

    /// <summary>The input set whose values prefill the form, if one was chosen.</summary>
    public InputSetDto? SelectedInputSet { get; set; }

    public bool IsSignedIn { get; set; }

    /// <summary>Author or admin: may edit, add versions, archive, delete.</summary>
    public bool CanManage { get; set; }
    public int TotalRuns { get; set; }
}

public class PromptIndexViewModel
{
    public List<PromptListDto> Prompts { get; set; } = new();
    public int Page { get; set; } = 1;
    public int PageCount { get; set; } = 1;
    public int TotalCount { get; set; }
    public bool ShowArchived { get; set; }
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

public class CompareVersionsViewModel
{
    public PromptDetailsDto Prompt { get; set; } = new();
    public PromptVersionDto From { get; set; } = new();
    public PromptVersionDto To { get; set; } = new();
    public DiffResult Diff { get; set; } = new([], 0, 0, 0);
    public IReadOnlyList<AvailableModel> Models { get; set; } = [];
    public string DefaultModel { get; set; } = string.Empty;

    public bool IsSignedIn { get; set; }

    /// <summary>Variables used by either version.</summary>
    public IReadOnlyList<string> Variables { get; set; } = [];
}

public class CompareRunsViewModel
{
    public RunDetailsDto Left { get; set; } = new();
    public RunDetailsDto Right { get; set; } = new();
    public DiffResult OutputDiff { get; set; } = new([], 0, 0, 0);
}
