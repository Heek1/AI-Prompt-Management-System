using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PromptManager.Data;
using PromptManager.Models;

namespace PromptManager.Api;

public sealed record ApiKeyInfo(int Id, string Name, string Prefix, DateTime CreatedAt, DateTime? LastUsedAt, bool IsActive);

public interface IApiKeyService
{
    /// <summary>Creates a key and returns it in full; it cannot be shown again.</summary>
    Task<(string Key, ApiKeyInfo Info)> CreateAsync(string ownerId, string name);

    Task<List<ApiKeyInfo>> ListAsync(string ownerId);

    Task<bool> RevokeAsync(string ownerId, int id);

    /// <summary>The active key matching the presented value, or null.</summary>
    Task<ApiKey?> ValidateAsync(string presented);
}

public sealed class ApiKeyService(AppDbContext db) : IApiKeyService
{
    public const string KeyPrefix = "pm_";
    public const int MaxKeysPerUser = 10;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    public async Task<(string Key, ApiKeyInfo Info)> CreateAsync(string ownerId, string name)
    {
        if (await db.ApiKeys.CountAsync(k => k.OwnerId == ownerId && k.RevokedAt == null) >= MaxKeysPerUser)
            throw new InvalidOperationException($"You can have at most {MaxKeysPerUser} active keys.");

        var key = KeyPrefix + RandomNumberGenerator.GetString(Alphabet, 40);
        var entity = new ApiKey
        {
            Name = name.Trim(),
            Prefix = key[..10],
            Hash = Hash(key),
            OwnerId = ownerId
        };
        db.ApiKeys.Add(entity);
        await db.SaveChangesAsync();
        return (key, ToInfo(entity));
    }

    public async Task<List<ApiKeyInfo>> ListAsync(string ownerId) =>
        (await db.ApiKeys.AsNoTracking().Where(k => k.OwnerId == ownerId).OrderByDescending(k => k.Id).ToListAsync())
        .Select(ToInfo).ToList();

    public async Task<bool> RevokeAsync(string ownerId, int id) =>
        await db.ApiKeys
            .Where(k => k.Id == id && k.OwnerId == ownerId && k.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, DateTime.UtcNow)) > 0;

    public async Task<ApiKey?> ValidateAsync(string presented)
    {
        if (string.IsNullOrWhiteSpace(presented) || !presented.StartsWith(KeyPrefix, StringComparison.Ordinal) || presented.Length > 100)
            return null;

        // Lookup by hash: the stored value is never compared to the plain key.
        var hash = Hash(presented);
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Hash == hash && k.RevokedAt == null);
        if (key is null) return null;

        // "Last used" is for people, so a minute of precision saves a write per request.
        if (key.LastUsedAt is null || key.LastUsedAt < DateTime.UtcNow.AddMinutes(-1))
        {
            key.LastUsedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        return key;
    }

    public static string Hash(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static ApiKeyInfo ToInfo(ApiKey k) => new(k.Id, k.Name, k.Prefix, k.CreatedAt, k.LastUsedAt, k.IsActive);
}
