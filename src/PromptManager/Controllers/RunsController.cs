using Microsoft.AspNetCore.Mvc;
using PromptManager.Infrastructure;
using PromptManager.Services;
using PromptManager.ViewModels;

namespace PromptManager.Controllers;

public class RunsController : Controller
{
    private readonly IPromptRunner _runner;

    public RunsController(IPromptRunner runner)
    {
        _runner = runner;
    }

    // GET /Runs?promptId=5&page=2
    public async Task<IActionResult> Index(int? promptId, int page = 1)
    {
        var runs = await _runner.ListRunsAsync(promptId, page);
        // Runs of a prompt that does not exist are a missing page, not an empty list.
        if (promptId is not null && runs.PromptTitle is null) return NotFound();
        return View(runs);
    }

    // GET /Runs/Compare?left=7&right=8
    public async Task<IActionResult> Compare(int left, int right)
    {
        var a = await _runner.GetRunAsync(left);
        var b = await _runner.GetRunAsync(right);
        if (a is null || b is null) return NotFound();

        return View(new CompareRunsViewModel
        {
            Left = a,
            Right = b,
            OutputDiff = TextDiff.Compare(a.Output, b.Output)
        });
    }

    // GET /Runs/Details/7
    public async Task<IActionResult> Details(int id)
    {
        var run = await _runner.GetRunAsync(id);
        return run is null ? NotFound() : View(run);
    }
}
