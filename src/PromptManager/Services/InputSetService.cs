using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PromptManager.Data;
using PromptManager.DTOs;

namespace PromptManager.Services;

public interface IInputSetService
{
    Task<List<InputSetDto>> ListAsync(int promptId);
    Task<InputSetDto?> GetAsync(int promptId, int id);

    /// <summary>Returns false when the set does not exist for that prompt.</summary>
    Task<bool> DeleteAsync(int promptId, int id);
}

/// <summary>Reads and deletes saved input sets; they are created by <see cref="PromptRunner"/> when a run asks to save its values.</summary>
public sealed class InputSetService(AppDbContext db) : IInputSetService
{
    public async Task<List<InputSetDto>> ListAsync(int promptId)
    {
        var rows = await db.PromptInputSets.AsNoTracking()
            .Where(s => s.PromptId == promptId)
            .OrderBy(s => s.Name)
            .ToListAsync();
        return rows.Select(s => new InputSetDto
        {
            Id = s.Id,
            Name = s.Name,
            Values = JsonSerializer.Deserialize<Dictionary<string, string>>(s.ValuesJson) ?? [],
            UpdatedAt = s.UpdatedAt
        }).ToList();
    }

    public async Task<InputSetDto?> GetAsync(int promptId, int id) =>
        (await ListAsync(promptId)).FirstOrDefault(s => s.Id == id);

    public async Task<bool> DeleteAsync(int promptId, int id) =>
        await db.PromptInputSets.Where(s => s.PromptId == promptId && s.Id == id).ExecuteDeleteAsync() > 0;
}
