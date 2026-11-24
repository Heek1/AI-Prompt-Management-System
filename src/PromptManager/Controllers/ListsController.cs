using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PromptManager.Models;
using PromptManager.Services;
using PromptManager.ViewModels;

namespace PromptManager.Controllers;

/// <summary>The categories and tools prompts can pick from; admins only.</summary>
[Authorize(Roles = Roles.Admin)]
public class ListsController : Controller
{
    private readonly ILookupService _lookups;
    private readonly Microsoft.Extensions.Localization.IStringLocalizer<PromptManager.SharedResource> _t;

    public ListsController(ILookupService lookups, Microsoft.Extensions.Localization.IStringLocalizer<PromptManager.SharedResource> t)
    {
        _t = t;
        _lookups = lookups;
    }

    // GET /Lists
    public async Task<IActionResult> Index() => View(new ListsViewModel
    {
        Categories = await _lookups.ListAsync(LookupKind.Category),
        Tools = await _lookups.ListAsync(LookupKind.Tool),
        CategoryUsage = await _lookups.UsageAsync(LookupKind.Category),
        ToolUsage = await _lookups.UsageAsync(LookupKind.Tool)
    });

    // POST /Lists/Add
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(LookupKind kind, string? name)
    {
        var error = await _lookups.AddAsync(kind, name ?? string.Empty);
        if (error is null)
            TempData["Success"] = _t["Added \"{0}\".", name!.Trim()].Value;
        else
            TempData["Error"] = error;
        return RedirectToAction(nameof(Index));
    }

    // POST /Lists/Remove
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove(int id)
    {
        if (!await _lookups.RemoveAsync(id)) return NotFound();
        TempData["Success"] = "Removed. Prompts that use it keep it.";
        return RedirectToAction(nameof(Index));
    }
}
