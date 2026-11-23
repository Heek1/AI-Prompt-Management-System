using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PromptManager.Api;
using PromptManager.Services;

namespace PromptManager.Controllers;

/// <summary>Each signed-in user manages their own API keys.</summary>
[Authorize]
public class ApiKeysController : Controller
{
    private readonly IApiKeyService _keys;
    private readonly ICurrentUser _user;

    public ApiKeysController(IApiKeyService keys, ICurrentUser user)
    {
        _keys = keys;
        _user = user;
    }

    public async Task<IActionResult> Index()
    {
        ViewData["NewKey"] = TempData["NewKey"];
        return View(await _keys.ListAsync(_user.Id!));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Required, StringLength(100, MinimumLength = 1)] string name)
    {
        if (!ModelState.IsValid)
        {
            TempData["Error"] = "Give the key a name (up to 100 characters).";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            var (key, _) = await _keys.CreateAsync(_user.Id!, name);
            // Shown once on the next page; only its hash is stored.
            TempData["NewKey"] = key;
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Revoke(int id)
    {
        if (await _keys.RevokeAsync(_user.Id!, id))
            TempData["Success"] = "Key revoked. Applications using it get 401 from now on.";
        else
            TempData["Error"] = "Key not found.";
        return RedirectToAction(nameof(Index));
    }
}
