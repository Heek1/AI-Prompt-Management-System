using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PromptManager.Services;

namespace PromptManager.Controllers;

/// <summary>Comments on versions, watching prompts and the inbox (C6).</summary>
[Authorize]
public class CollaborationController : Controller
{
    private readonly ICollaborationService _collab;

    public CollaborationController(ICollaborationService collab)
    {
        _collab = collab;
    }

    // POST /Prompts/Comment
    [HttpPost("/Prompts/Comment")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Comment(int promptId, int versionNumber, string? body)
    {
        if (await _collab.AddCommentAsync(promptId, versionNumber, body) is { } error)
            TempData["Error"] = error;
        return Redirect($"/Prompts/Details/{promptId}#v{versionNumber}");
    }

    // POST /Prompts/DeleteComment
    [HttpPost("/Prompts/DeleteComment")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteComment(int promptId, int commentId, int versionNumber)
    {
        if (!await _collab.DeleteCommentAsync(commentId)) return Forbid();
        return Redirect($"/Prompts/Details/{promptId}#v{versionNumber}");
    }

    // POST /Prompts/Watch
    [HttpPost("/Prompts/Watch")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Watch(int promptId, bool watch)
    {
        await _collab.SetWatchingAsync(promptId, watch);
        TempData["Success"] = watch
            ? "Watching: new versions, promotions and comments will appear in your inbox."
            : "Not watching any more.";
        return Redirect($"/Prompts/Details/{promptId}");
    }

    // GET /Inbox
    [HttpGet("/Inbox")]
    public async Task<IActionResult> Inbox()
    {
        var items = await _collab.InboxAsync();
        // Opening the inbox reads it; the list still shows which ones were new.
        await _collab.MarkAllReadAsync();
        return View(items);
    }
}
