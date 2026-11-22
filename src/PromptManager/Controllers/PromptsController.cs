using Microsoft.AspNetCore.Mvc;
using PromptManager.DTOs;
using PromptManager.Infrastructure;
using PromptManager.Llm;
using PromptManager.Services;
using PromptManager.ViewModels;

namespace PromptManager.Controllers;
public class PromptsController : Controller
{
    private readonly IPromptService _service;
    private readonly IPromptRunner _runner;
    private readonly LlmCatalog _catalog;

    public PromptsController(IPromptService service, IPromptRunner runner, LlmCatalog catalog)
    {
        _service = service;
        _runner = runner;
        _catalog = catalog;
    }

    // GET /Prompts
    public async Task<IActionResult> Index(string? search, string? category, string? aiTool, string sortBy = "rating")
    {
        var prompts = await _service.GetAllAsync(search, category, aiTool, sortBy);

        var vm = new PromptIndexViewModel
        {
            Prompts = prompts,
            SearchQuery = search,
            CategoryFilter = category,
            AiToolFilter = aiTool,
            SortBy = sortBy,
            Categories = PromptCategories.All,
            AiTools = AiTools.All
        };

        return View(vm);
    }

    // GET /Prompts/Create
    public IActionResult Create()
    {
        ViewBag.Categories = PromptCategories.All;
        ViewBag.AiTools = AiTools.All;
        return View(new CreatePromptDto());
    }

    // POST /Prompts/Create
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreatePromptDto dto)
    {
        if (!ModelState.IsValid)
        {
            ViewBag.Categories = PromptCategories.All;
            ViewBag.AiTools = AiTools.All;
            return View(dto);
        }

        var id = await _service.CreateAsync(dto);
        TempData["Success"] = "Prompt created successfully!";
        return RedirectToAction(nameof(Details), new { id });
    }

    // GET /Prompts/Details/5
    public async Task<IActionResult> Details(int id)
    {
        var prompt = await _service.GetByIdAsync(id);
        if (prompt == null) return NotFound();

        var runs = await _runner.ListRunsAsync(id, page: 1, pageSize: 5);
        var vm = new PromptDetailsViewModel
        {
            Prompt = prompt,
            Versions = prompt.Versions,
            RateForm = new RatePromptDto { PromptId = id },
            NewVersionForm = new CreatePromptVersionDto { PromptId = id },
            RunForm = new RunPromptDto { PromptId = id, VersionNumber = prompt.CurrentVersion, Model = _catalog.DefaultModel },
            Models = _catalog.List(),
            RecentRuns = runs.Runs,
            TotalRuns = runs.TotalCount
        };

        return View(vm);
    }

    // GET /Prompts/CreateVersion/5
    public async Task<IActionResult> CreateVersion(int id)
    {
        var prompt = await _service.GetByIdAsync(id);
        if (prompt == null) return NotFound();

        ViewBag.Prompt = prompt;
        return View(new CreatePromptVersionDto { PromptId = id });
    }

    // POST /Prompts/CreateVersion
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateVersion(CreatePromptVersionDto dto)
    {
        if (!ModelState.IsValid)
        {
            ViewBag.Prompt = await _service.GetByIdAsync(dto.PromptId);
            return View(dto);
        }

        var success = await _service.CreateVersionAsync(dto);
        if (!success)
        {
            ModelState.AddModelError("", "Could not create version. Prompt may be archived or not found.");
            ViewBag.Prompt = await _service.GetByIdAsync(dto.PromptId);
            return View(dto);
        }

        TempData["Success"] = "New version created!";
        return RedirectToAction(nameof(Details), new { id = dto.PromptId });
    }

    // GET /Prompts/Rate/5
    public async Task<IActionResult> Rate(int id)
    {
        var prompt = await _service.GetByIdAsync(id);
        if (prompt == null) return NotFound();

        ViewBag.Prompt = prompt;
        return View(new RatePromptDto { PromptId = id });
    }

    // POST /Prompts/Rate
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Rate(RatePromptDto dto)
    {
        if (!ModelState.IsValid)
        {
            ViewBag.Prompt = await _service.GetByIdAsync(dto.PromptId);
            return View(dto);
        }

        var (success, error) = await _service.RateAsync(dto);
        if (!success)
        {
            ModelState.AddModelError("", error);
            ViewBag.Prompt = await _service.GetByIdAsync(dto.PromptId);
            return View(dto);
        }

        TempData["Success"] = "Rating submitted!";
        return RedirectToAction(nameof(Details), new { id = dto.PromptId });
    }

    // POST /Prompts/UsePrompt
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UsePrompt(UsePromptDto dto)
    {
        var (success, error) = await _service.UsePromptAsync(dto);

        if (!success)
        {
            TempData["Error"] = error;
        }
        else
        {
            TempData["Success"] = "Prompt usage recorded!";
        }

        return RedirectToAction(nameof(Details), new { id = dto.PromptId });
    }

    // POST /Prompts/Archive/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Archive(int id)
    {
        var success = await _service.ArchiveAsync(id);
        if (!success)
        {
            TempData["Error"] = "Prompt not found.";
            return RedirectToAction(nameof(Index));
        }

        TempData["Success"] = "Prompt archived.";
        return RedirectToAction(nameof(Index));
    }

    // POST /Prompts/Run
    // Runs synchronously within the request; long evaluations will move to a
    // background queue later. The run is stored even when the provider fails.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Run(RunPromptDto dto, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            TempData["Error"] = "Choose a model to run the prompt on.";
            return RedirectToAction(nameof(Details), new { id = dto.PromptId });
        }

        var (runId, error) = await _runner.RunAsync(dto, ct);
        if (runId is null)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Details), new { id = dto.PromptId });
        }

        return RedirectToAction("Details", "Runs", new { id = runId });
    }
}
