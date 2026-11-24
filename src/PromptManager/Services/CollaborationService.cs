using Microsoft.EntityFrameworkCore;
using PromptManager.Data;
using PromptManager.Models;

namespace PromptManager.Services;

public sealed record CommentDto(int Id, int VersionNumber, string Body, string AuthorName, string AuthorId, DateTime CreatedAt);

public sealed record NotificationDto(int Id, string Text, string Url, DateTime CreatedAt, bool IsRead);

public interface ICollaborationService
{
    Task<Dictionary<int, List<CommentDto>>> CommentsByVersionAsync(int promptId);

    /// <summary>Null on success, otherwise the reason.</summary>
    Task<string?> AddCommentAsync(int promptId, int versionNumber, string? body);
    Task<bool> DeleteCommentAsync(int commentId);

    Task<bool> IsWatchingAsync(int promptId);
    Task SetWatchingAsync(int promptId, bool watch);

    /// <summary>Tells everyone watching the prompt, except the person who acted.</summary>
    Task NotifyWatchersAsync(int promptId, string text, string url);

    Task<int> UnreadCountAsync();
    Task<List<NotificationDto>> InboxAsync(int take = 50);
    Task MarkAllReadAsync();
}

/// <summary>
/// Comments on versions and in-app notifications (C6). Notifications stay in
/// the app: there is no e-mail, because the instance has no mail service.
/// </summary>
public sealed class CollaborationService(AppDbContext db, ICurrentUser user) : ICollaborationService
{
    public const int MaxCommentLength = 2_000;

    public async Task<Dictionary<int, List<CommentDto>>> CommentsByVersionAsync(int promptId) =>
        (await db.VersionComments.AsNoTracking()
            .Where(c => c.PromptId == promptId)
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .Select(c => new CommentDto(c.Id, c.VersionNumber, c.Body, c.AuthorName, c.AuthorId, c.CreatedAt))
            .ToListAsync())
        .GroupBy(c => c.VersionNumber)
        .ToDictionary(g => g.Key, g => g.ToList());

    public async Task<string?> AddCommentAsync(int promptId, int versionNumber, string? body)
    {
        if (!user.IsSignedIn) return "Sign in to comment.";
        body = body?.Trim() ?? "";
        if (body.Length == 0) return "Write something first.";
        if (body.Length > MaxCommentLength) return $"A comment can have at most {MaxCommentLength} characters.";

        var prompt = await db.Prompts.AsNoTracking().Select(p => new { p.Id, p.Title, p.IsArchived }).FirstOrDefaultAsync(p => p.Id == promptId);
        if (prompt is null) return "Prompt not found.";
        if (prompt.IsArchived) return "Archived prompts cannot be commented on.";
        if (!await db.PromptVersions.AnyAsync(v => v.PromptId == promptId && v.VersionNumber == versionNumber))
            return $"Version {versionNumber} does not exist.";

        var name = await user.GetDisplayNameAsync();
        db.VersionComments.Add(new VersionComment
        {
            PromptId = promptId, VersionNumber = versionNumber, Body = body, AuthorName = name, AuthorId = user.Id!
        });
        await db.SaveChangesAsync();

        // Taking part in a discussion means wanting to hear the replies.
        await SetWatchingAsync(promptId, true);
        await NotifyWatchersAsync(promptId, $"{name} commented on v{versionNumber} of {prompt.Title}: \"{Snippet(body)}\"",
            $"/Prompts/Details/{promptId}#v{versionNumber}");
        return null;
    }

    public async Task<bool> DeleteCommentAsync(int commentId)
    {
        var comment = await db.VersionComments.FirstOrDefaultAsync(c => c.Id == commentId);
        if (comment is null || !(user.IsAdmin || (user.IsSignedIn && comment.AuthorId == user.Id))) return false;
        db.VersionComments.Remove(comment);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> IsWatchingAsync(int promptId) =>
        user.Id is { } id && await db.PromptWatches.AnyAsync(w => w.PromptId == promptId && w.UserId == id);

    public async Task SetWatchingAsync(int promptId, bool watch)
    {
        if (user.Id is not { } id) return;
        var existing = await db.PromptWatches.FirstOrDefaultAsync(w => w.PromptId == promptId && w.UserId == id);
        if (watch && existing is null)
        {
            db.PromptWatches.Add(new PromptWatch { PromptId = promptId, UserId = id });
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // A second tab watched it at the same moment; it is watched either way.
                db.ChangeTracker.Clear();
            }
        }
        else if (!watch && existing is not null)
        {
            db.PromptWatches.Remove(existing);
            await db.SaveChangesAsync();
        }
    }

    public async Task NotifyWatchersAsync(int promptId, string text, string url)
    {
        var actor = user.Id;
        var watchers = await db.PromptWatches.AsNoTracking()
            .Where(w => w.PromptId == promptId && w.UserId != actor)
            .Select(w => w.UserId)
            .ToListAsync();
        if (watchers.Count == 0) return;

        var now = DateTime.UtcNow;
        db.Notifications.AddRange(watchers.Select(id => new Notification
        {
            UserId = id, PromptId = promptId, Text = text.Length > 500 ? text[..500] : text, Url = url, CreatedAt = now
        }));
        await db.SaveChangesAsync();
    }

    public async Task<int> UnreadCountAsync() =>
        user.Id is { } id ? await db.Notifications.CountAsync(n => n.UserId == id && n.ReadAt == null) : 0;

    public async Task<List<NotificationDto>> InboxAsync(int take = 50) =>
        user.Id is not { } id
            ? []
            : await db.Notifications.AsNoTracking()
                .Where(n => n.UserId == id)
                .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
                .Take(take)
                .Select(n => new NotificationDto(n.Id, n.Text, n.Url, n.CreatedAt, n.ReadAt != null))
                .ToListAsync();

    public async Task MarkAllReadAsync()
    {
        if (user.Id is not { } id) return;
        await db.Notifications.Where(n => n.UserId == id && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, DateTime.UtcNow));
    }

    private static string Snippet(string s)
    {
        var line = s.ReplaceLineEndings(" ");
        return line.Length <= 80 ? line : line[..80] + "…";
    }
}
