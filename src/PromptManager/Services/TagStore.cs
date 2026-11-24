using Microsoft.EntityFrameworkCore;
using PromptManager.Data;
using PromptManager.Infrastructure;
using PromptManager.Models;

namespace PromptManager.Services;

public static class TagStore
{
    /// <summary>
    /// The tags named in a comma-separated field, existing ones reused by slug
    /// and new ones created (but not saved) here. Invalid input is rejected by
    /// the caller; anything left over is cut to the limit.
    /// </summary>
    public static async Task<List<Tag>> ResolveAsync(AppDbContext db, string? input)
    {
        var parsed = TagNames.Parse(input).Tags.Take(TagNames.MaxTags).ToList();
        if (parsed.Count == 0) return [];

        var slugs = parsed.Select(t => t.Slug).ToList();
        var existing = await db.Tags.Where(t => slugs.Contains(t.Slug)).ToDictionaryAsync(t => t.Slug);
        return parsed.Select(t => existing.TryGetValue(t.Slug, out var tag) ? tag : new Tag { Name = t.Name, Slug = t.Slug }).ToList();
    }
}
