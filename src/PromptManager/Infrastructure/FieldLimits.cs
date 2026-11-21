namespace PromptManager.Infrastructure;

/// <summary>
/// Maximum lengths shared by the form DTOs and the EF model, so validation and
/// the database schema cannot drift apart.
/// </summary>
public static class FieldLimits
{
    public const int Title = 200;
    public const int Description = 1000;
    public const int PromptTextMin = 20;
    public const int PromptText = 20_000;
    public const int Category = 100;
    public const int AiTool = 50;
    public const int PersonName = 100;
    public const int ChangeNote = 500;
    public const int Comment = 1000;
}
