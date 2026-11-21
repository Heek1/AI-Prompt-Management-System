using Microsoft.AspNetCore.Mvc;
using PromptManager.Services;

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
        return View(await _runner.ListRunsAsync(promptId, page));
    }

    // GET /Runs/Details/7
    public async Task<IActionResult> Details(int id)
    {
        var run = await _runner.GetRunAsync(id);
        return run is null ? NotFound() : View(run);
    }
}
