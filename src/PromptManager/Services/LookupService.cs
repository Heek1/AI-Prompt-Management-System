using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PromptManager.Data;
using PromptManager.Infrastructure;
using PromptManager.Models;

namespace PromptManager.Services;

public interface ILookupService
{
    /// <summary>The names in a list, alphabetically.</summary>
    Task<List<string>> NamesAsync(LookupKind kind);

    Task<List<LookupValue>> ListAsync(LookupKind kind);
    Task<bool> IsAllowedAsync(LookupKind kind, string? name);

    /// <summary>Null on success, otherwise the reason for the form.</summary>
    Task<string?> AddAsync(LookupKind kind, string name);
    Task<bool> RemoveAsync(int id);

    /// <summary>How many prompts, archived included, use each name of the list.</summary>
    Task<Dictionary<string, int>> UsageAsync(LookupKind kind);
}

/// <summary>
/// Categories and tools, managed by admins. Every prompt form reads them, so
/// they are cached in memory and the cache is dropped on each change. With
/// several instances another instance may show a stale list for up to
/// <see cref="CacheFor"/>; validation then still uses that list, which is
/// acceptable for a list that changes rarely.
/// </summary>
public class LookupService : ILookupService
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;

    public LookupService(AppDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    private static string Key(LookupKind kind) => "lookup:" + kind;

    public async Task<List<LookupValue>> ListAsync(LookupKind kind) =>
        (await _cache.GetOrCreateAsync(Key(kind), entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheFor;
            return _db.LookupValues.AsNoTracking()
                .Where(v => v.Kind == kind)
                .OrderBy(v => v.Name)
                .ToListAsync();
        }))!;

    public async Task<List<string>> NamesAsync(LookupKind kind) =>
        (await ListAsync(kind)).Select(v => v.Name).ToList();

    public async Task<bool> IsAllowedAsync(LookupKind kind, string? name) =>
        !string.IsNullOrEmpty(name) && (await ListAsync(kind)).Any(v => v.Name == name);

    public async Task<string?> AddAsync(LookupKind kind, string name)
    {
        name = name.Trim();
        var limit = kind == LookupKind.Category ? FieldLimits.Category : FieldLimits.AiTool;
        if (name.Length == 0) return "Enter a name.";
        if (name.Length > limit) return $"A name can have at most {limit} characters.";

        if (await _db.LookupValues.AnyAsync(v => v.Kind == kind && v.Name.ToLower() == name.ToLower()))
            return $"\"{name}\" is already in the list.";

        _db.LookupValues.Add(new LookupValue { Kind = kind, Name = name });
        await _db.SaveChangesAsync();
        _cache.Remove(Key(kind));
        return null;
    }

    public async Task<bool> RemoveAsync(int id)
    {
        var value = await _db.LookupValues.FirstOrDefaultAsync(v => v.Id == id);
        if (value is null) return false;

        _db.LookupValues.Remove(value);
        await _db.SaveChangesAsync();
        _cache.Remove(Key(value.Kind));
        return true;
    }

    public async Task<Dictionary<string, int>> UsageAsync(LookupKind kind)
    {
        var counts = kind == LookupKind.Category
            ? _db.Prompts.GroupBy(p => p.Category).Select(g => new { Name = g.Key, Count = g.Count() })
            : _db.Prompts.GroupBy(p => p.AiTool).Select(g => new { Name = g.Key, Count = g.Count() });
        return await counts.ToDictionaryAsync(c => c.Name, c => c.Count);
    }
}
