using Azure;
using Azure.Data.Tables;

namespace AI_Prompt_Management_System.Models;

public class PromptUsageHistoryEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty; 
    public string RowKey { get; set; } = string.Empty;        
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string PromptId { get; set; } = string.Empty;
    public string PromptTitle { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string AiTool { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public DateTime UsedAt { get; set; }
}
