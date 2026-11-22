using Microsoft.EntityFrameworkCore;
using PromptManager.Models;

namespace PromptManager.Data;

/// <summary>
/// Fills an empty development database with a few sample prompts so the UI
/// has something to show on first run. The database file is not committed,
/// so this replaces the sample data that used to live in git.
/// </summary>
public static class DevDataSeeder
{
    public static async Task SeedAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (await db.Prompts.AnyAsync(ct))
            return;

        var now = DateTime.UtcNow;

        db.Prompts.AddRange(
            Sample(
                title: "Code review checklist",
                description: "Asks for a structured review of a diff.",
                text: "Review the following diff. List correctness bugs first, then security issues, then readability. Quote the exact line for each finding.",
                category: "Code Generation",
                aiTool: "Claude",
                createdAt: now.AddDays(-3)),
            Sample(
                title: "Unit tests for a method",
                description: "Generates xUnit tests including edge cases.",
                text: "Write xUnit tests for the method below. Cover the happy path, boundary values and invalid input. Use descriptive test names.",
                category: "Testing",
                aiTool: "ChatGPT",
                createdAt: now.AddDays(-2)),
            Sample(
                title: "Explain an error message",
                description: "Turns a stack trace into a plain explanation and a fix.",
                text: "Explain what this error means in plain language, list the most likely causes, and suggest how to confirm each one.",
                category: "Bug Fixing",
                aiTool: "Gemini",
                createdAt: now.AddDays(-1)));

        await db.SaveChangesAsync(ct);
    }

    private static Prompt Sample(string title, string description, string text, string category, string aiTool, DateTime createdAt)
    {
        const string author = "sample";
        return new Prompt
        {
            Title = title,
            Description = description,
            CurrentText = text,
            Category = category,
            AiTool = aiTool,
            Author = author,
            CurrentVersion = 1,
            ProductionVersion = 1,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            Versions =
            {
                new PromptVersion
                {
                    VersionNumber = 1,
                    PromptText = text,
                    ChangeNote = "Initial version",
                    CreatedBy = author,
                    CreatedAt = createdAt,
                    Status = VersionStatus.Production,
                    PromotedAt = createdAt,
                    PromotedBy = author
                }
            }
        };
    }
}
