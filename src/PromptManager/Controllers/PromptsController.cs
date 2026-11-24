using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PromptManager.DTOs;
using PromptManager.Infrastructure;
using PromptManager.Llm;
using PromptManager.Models;
using PromptManager.Services;
using PromptManager.ViewModels;

namespace PromptManager.Controllers;

/// <summary>
/// Reading is open to everyone. Creating, rating and running need an account;
/// changing or removing a prompt needs its author or an admin.
/// </summary>
public class PromptsController : Controller
{
    private const int PageSize = 12;

    private readonly IPromptService _service;
    private readonly IPromptRunner _runner;
    private readonly LlmCatalog _catalog;
    private readonly IInputSetService _inputSets;
    private readonly ICurrentUser _user;
    private readonly IVersionLifecycle _lifecycle;
    private readonly ILookupService _lookups;
    private readonly ICollaborationService _collab;
    private readonly Microsoft.Extensions.Localization.IStringLocalizer<PromptManager.SharedResource> _t;

    public PromptsController(IPromptService service, IPromptRunner runner, LlmCatalog catalog, IInputSetService inputSets,
        ICurrentUser user, IVersionLifecycle lifecycle, ILookupService lookups, ICollaborationService collab,
        Microsoft.Extensions.Localization.IStringLocalizer<PromptManager.SharedResource> t)
    {
        _t = t;
        _collab = collab;
        _lifecycle = lifecycle;
        _lookups = lookups;
        _service = service;
        _runner = runner;
        _catalog = catalog;
        _inputSets = inputSets;
        _user = user;
    }

    // GET /Prompts?search=&category=&aiTool=&tag=&sortBy=&showArchived=&page=
    public async Task<IActionResult> Index(string? search, string? category, string? aiTool, string? tag,
        string? sortBy = null, bool showArchived = false, int page = 1)
    {
        // A search is ordered by how well prompts match unless another order was picked.
        sortBy ??= string.IsNullOrWhiteSpace(search) ? "rating" : "relevance";
        var result = await _service.GetPageAsync(search, category, aiTool, sortBy, showArchived, page, PageSize, tag);
        var tagCounts = await _service.TagCountsAsync(showArchived);

        return View(new PromptIndexViewModel
        {
            Prompts = result.Prompts,
            Page = result.Page,
            PageCount = result.PageCount,
            TotalCount = result.TotalCount,
            SearchQuery = search,
            CategoryFilter = category,
            AiToolFilter = aiTool,
            TagFilter = tag,
            ActiveTag = tagCounts.FirstOrDefault(t => t.Slug == tag),
            TagCounts = tagCounts,
            SortBy = sortBy,
            ShowArchived = showArchived,
            Categories = await _lookups.NamesAsync(LookupKind.Category),
            AiTools = await _lookups.NamesAsync(LookupKind.Tool)
        });
    }

    // GET /Prompts/Create
    [Authorize]
    public async Task<IActionResult> Create()
    {
        await FillListsAsync();
        return View(new CreatePromptDto());
    }

    // POST /Prompts/Create
    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreatePromptDto dto)
    {
        await ValidateListsAsync(dto.Category, dto.AiTool, dto.Tags);
        if (!ModelState.IsValid)
        {
            await FillListsAsync();
            return View(dto);
        }

        dto.Author = await _user.GetDisplayNameAsync();
        dto.OwnerId = _user.Id;
        var id = await _service.CreateAsync(dto);
        await _collab.SetWatchingAsync(id, true);
        TempData["Success"] = "Prompt created successfully!";
        return RedirectToAction(nameof(Details), new { id });
    }

    // GET /Prompts/Details/5?inputSet=3
    public async Task<IActionResult> Details(int id, int? inputSet)
    {
        var prompt = await _service.GetByIdAsync(id);
        if (prompt == null) return NotFound();

        var runs = await _runner.ListRunsAsync(id, page: 1, pageSize: 5);
        var inputSets = await _inputSets.ListAsync(id);
        var variables = PromptTemplate.ExtractVariables(prompt.CurrentText)
            .Concat(prompt.Versions.SelectMany(v => PromptTemplate.ExtractVariables(v.PromptText)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var models = ModelsForCurrentUser();

        return View(new PromptDetailsViewModel
        {
            Prompt = prompt,
            Versions = prompt.Versions,
            RateForm = new RatePromptDto { PromptId = id },
            NewVersionForm = new CreatePromptVersionDto { PromptId = id },
            RunForm = new RunPromptDto { PromptId = id, VersionNumber = prompt.CurrentVersion, Model = DefaultModel(models) },
            Models = models,
            RecentRuns = runs.Runs,
            TotalRuns = runs.TotalCount,
            Variables = variables,
            InputSets = inputSets,
            SelectedInputSet = inputSet is { } setId ? inputSets.FirstOrDefault(s => s.Id == setId) : null,
            IsSignedIn = _user.IsSignedIn,
            CanManage = _user.CanManage(prompt.OwnerId),
            IsAdmin = _user.IsAdmin,
            BlockedPromotion = TempData["BlockedPromotion"] as int?,
            Comments = await _collab.CommentsByVersionAsync(id),
            IsWatching = await _collab.IsWatchingAsync(id),
            CurrentUserId = _user.Id,
            MyRating = _user.Id is { } me ? await _service.RatingByAsync(id, me) : null
        });
    }

    // GET /Prompts/Edit/5
    [Authorize]
    public async Task<IActionResult> Edit(int id)
    {
        var prompt = await _service.GetByIdAsync(id);
        if (prompt == null) return NotFound();
        if (!_user.CanManage(prompt.OwnerId)) return Forbid();
        if (prompt.IsArchived) return ArchivedRedirect(prompt.Id);

        await FillListsAsync(prompt.Category, prompt.AiTool);
        return View(new EditPromptDto
        {
            Id = prompt.Id,
            Title = prompt.Title,
            Description = prompt.Description,
            Category = prompt.Category,
            AiTool = prompt.AiTool,
            Tags = string.Join(", ", prompt.Tags.Select(t => t.Name))
        });
    }

    // POST /Prompts/Edit
    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(EditPromptDto dto)
    {
        var prompt = await _service.GetByIdAsync(dto.Id);
        if (prompt == null) return NotFound();
        if (!_user.CanManage(prompt.OwnerId)) return Forbid();
        if (prompt.IsArchived) return ArchivedRedirect(prompt.Id);

        // A prompt may keep a category or tool an admin has since removed.
        await ValidateListsAsync(dto.Category == prompt.Category ? null : dto.Category,
            dto.AiTool == prompt.AiTool ? null : dto.AiTool, dto.Tags);
        if (!ModelState.IsValid)
        {
            await FillListsAsync(prompt.Category, prompt.AiTool);
            return View(dto);
        }

        await _service.UpdateAsync(dto);
        TempData["Success"] = "Prompt updated.";
        return RedirectToAction(nameof(Details), new { id = dto.Id });
    }

    // GET /Prompts/CreateVersion/5
    [Authorize]
    public async Task<IActionResult> CreateVersion(int id)
    {
        var prompt = await _service.GetByIdAsync(id);
        if (prompt == null) return NotFound();
        if (!_user.CanManage(prompt.OwnerId)) return Forbid();
        if (prompt.IsArchived) return ArchivedRedirect(prompt.Id);

        ViewBag.Prompt = prompt;
        // Start from the current text: most versions are small edits of it.
        return View(new CreatePromptVersionDto { PromptId = id, PromptText = prompt.CurrentText });
    }

    // POST /Prompts/CreateVersion
    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateVersion(CreatePromptVersionDto dto)
    {
        var prompt = await _service.GetByIdAsync(dto.PromptId);
        if (prompt == null) return NotFound();
        if (!_user.CanManage(prompt.OwnerId)) return Forbid();

        if (!ModelState.IsValid)
        {
            ViewBag.Prompt = prompt;
            return View(dto);
        }

        dto.CreatedBy = await _user.GetDisplayNameAsync();
        dto.CreatedById = _user.Id;
        if (!await _service.CreateVersionAsync(dto))
        {
            ModelState.AddModelError("", _t.Message("Could not create version. Prompt may be archived or not found."));
            ViewBag.Prompt = await _service.GetByIdAsync(dto.PromptId);
            return View(dto);
        }

        var created = await _service.GetByIdAsync(dto.PromptId);
        await _collab.NotifyWatchersAsync(dto.PromptId,
            $"{dto.CreatedBy} added v{created!.CurrentVersion} of {created.Title}" + (string.IsNullOrWhiteSpace(dto.ChangeNote) ? "." : $": {dto.ChangeNote}"),
            $"/Prompts/Details/{dto.PromptId}#v{created.CurrentVersion}");
        TempData["Success"] = "New version created!";
        return RedirectToAction(nameof(Details), new { id = dto.PromptId });
    }

    // GET /Prompts/Rate/5
    [Authorize]
    public async Task<IActionResult> Rate(int id)
    {
        var prompt = await _service.GetByIdAsync(id);
        if (prompt == null) return NotFound();
        if (prompt.IsArchived) return ArchivedRedirect(prompt.Id);
        // One rating per account: say so before the form, not after it.
        if (await _service.RatingByAsync(id, _user.Id!) is not null)
        {
            TempData["Error"] = "You have already rated this prompt.";
            return RedirectToAction(nameof(Details), new { id });
        }

        ViewBag.Prompt = prompt;
        return View(new RatePromptDto { PromptId = id });
    }

    // POST /Prompts/Rate
    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Rate(RatePromptDto dto)
    {
        if (!ModelState.IsValid)
        {
            ViewBag.Prompt = await _service.GetByIdAsync(dto.PromptId);
            return View(dto);
        }

        dto.UserId = _user.Id!;
        dto.UserName = await _user.GetDisplayNameAsync();
        var (success, error) = await _service.RateAsync(dto);
        if (!success)
        {
            ModelState.AddModelError("", _t.Message(error));
            ViewBag.Prompt = await _service.GetByIdAsync(dto.PromptId);
            return View(dto);
        }

        TempData["Success"] = "Rating submitted!";
        return RedirectToAction(nameof(Details), new { id = dto.PromptId });
    }

    // POST /Prompts/Promote  (also rolls back to an older version)
    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Promote(int id, int versionNumber, bool overrideRegression = false)
    {
        var prompt = await _service.GetByIdAsync(id);
        if (prompt == null) return NotFound();
        if (!_user.CanManage(prompt.OwnerId)) return Forbid();

        // Only admins may push a version whose evals are worse than production's.
        var result = await _lifecycle.PromoteAsync(id, versionNumber, await _user.GetDisplayNameAsync(),
            overrideRegression && _user.IsAdmin);

        TempData[result.Promoted ? "Success" : "Error"] = result.Message;
        if (result.Promoted)
            await _collab.NotifyWatchersAsync(id, $"{await _user.GetDisplayNameAsync()} put v{versionNumber} of {prompt.Title} in production.",
                $"/Prompts/Details/{id}#v{versionNumber}");
        if (result.BlockedByRegression)
            TempData["BlockedPromotion"] = versionNumber;
        return RedirectToAction(nameof(Details), new { id });
    }

    // POST /Prompts/Archive
    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Archive(int id) =>
        Manage(id, () => _service.ArchiveAsync(id), "Prompt archived.", toIndex: true);

    // POST /Prompts/Unarchive
    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Unarchive(int id) =>
        Manage(id, () => _service.UnarchiveAsync(id), "Prompt restored from the archive.", toIndex: false);

    // POST /Prompts/Delete
    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Delete(int id) =>
        Manage(id, () => _service.DeleteAsync(id), "Prompt deleted with its versions, ratings and runs.", toIndex: true);

    private async Task<IActionResult> Manage(int id, Func<Task<bool>> action, string done, bool toIndex)
    {
        var prompt = await _service.GetByIdAsync(id);
        if (prompt == null) return NotFound();
        if (!_user.CanManage(prompt.OwnerId)) return Forbid();

        await action();
        TempData["Success"] = done;
        return toIndex ? RedirectToAction(nameof(Index)) : RedirectToAction(nameof(Details), new { id });
    }

    // POST /Prompts/Run
    // Runs synchronously within the request. The run is stored even when the provider fails.
    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Run(RunPromptDto dto, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            TempData["Error"] = "Choose a model to run the prompt on.";
            return RedirectToAction(nameof(Details), new { id = dto.PromptId });
        }

        await StampRunnerAsync(dto);
        var (runId, error) = await _runner.RunAsync(dto, ct);
        if (runId is null)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Details), new { id = dto.PromptId });
        }

        return RedirectToAction("Details", "Runs", new { id = runId });
    }

    // GET /Prompts/Compare/5?from=1&to=3
    public async Task<IActionResult> Compare(int id, int? from, int? to)
    {
        var prompt = await _service.GetByIdAsync(id);
        if (prompt == null) return NotFound();
        if (prompt.Versions.Count < 2)
        {
            TempData["Error"] = "This prompt has only one version.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var toVersion = prompt.Versions.FirstOrDefault(v => v.VersionNumber == (to ?? prompt.CurrentVersion));
        var fromVersion = prompt.Versions.FirstOrDefault(v => v.VersionNumber == (from ?? (toVersion?.VersionNumber ?? 1) - 1));
        if (fromVersion is null || toVersion is null) return NotFound();

        var models = ModelsForCurrentUser();
        return View(new CompareVersionsViewModel
        {
            Prompt = prompt,
            From = fromVersion,
            To = toVersion,
            Diff = TextDiff.Compare(fromVersion.PromptText, toVersion.PromptText),
            Models = models,
            DefaultModel = DefaultModel(models),
            IsSignedIn = _user.IsSignedIn,
            Variables = PromptTemplate.ExtractVariables(fromVersion.PromptText)
                .Concat(PromptTemplate.ExtractVariables(toVersion.PromptText))
                .Distinct(StringComparer.Ordinal)
                .ToList()
        });
    }

    // POST /Prompts/RunCompare
    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RunCompare(CompareRunDto dto, CancellationToken ct)
    {
        var back = RedirectToAction(nameof(Compare), new { id = dto.PromptId, from = dto.FromVersion, to = dto.ToVersion });
        if (!ModelState.IsValid)
        {
            TempData["Error"] = "Choose a model to run both versions on.";
            return back;
        }

        // Check both versions before running either, so a missing value for
        // the second one cannot leave a lone, paid-for first run behind.
        var prompt = await _service.GetByIdAsync(dto.PromptId);
        if (prompt == null) return NotFound();
        var values = dto.Variables.ToDictionary(kv => kv.Key, kv => kv.Value ?? string.Empty);
        foreach (var number in new[] { dto.FromVersion, dto.ToVersion })
        {
            var version = prompt.Versions.FirstOrDefault(v => v.VersionNumber == number);
            if (version is null) return NotFound();
            var missing = PromptTemplate.Render(version.PromptText, values).Missing;
            if (missing.Count > 0)
            {
                TempData["Error"] = $"Fill in a value for: {string.Join(", ", missing)}.";
                return back;
            }
        }

        var runIds = new List<int>();
        foreach (var number in new[] { dto.FromVersion, dto.ToVersion })
        {
            var run = new RunPromptDto
            {
                PromptId = dto.PromptId,
                VersionNumber = number,
                Model = dto.Model,
                Variables = dto.Variables
            };
            await StampRunnerAsync(run);
            var (runId, error) = await _runner.RunAsync(run, ct);

            if (runId is null)
            {
                TempData["Error"] = error;
                return runIds.Count == 0 ? back : RedirectToAction("Details", "Runs", new { id = runIds[0] });
            }
            runIds.Add(runId.Value);
        }

        return RedirectToAction("Compare", "Runs", new { left = runIds[0], right = runIds[1] });
    }

    // POST /Prompts/DeleteInputSet
    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteInputSet(int promptId, int inputSetId)
    {
        var prompt = await _service.GetByIdAsync(promptId);
        if (prompt == null) return NotFound();
        if (!_user.CanManage(prompt.OwnerId)) return Forbid();

        if (await _inputSets.DeleteAsync(promptId, inputSetId))
            TempData["Success"] = "Input set deleted.";
        else
            TempData["Error"] = "Input set not found.";
        return RedirectToAction(nameof(Details), new { id = promptId });
    }

    private async Task StampRunnerAsync(RunPromptDto dto)
    {
        dto.RunBy = await _user.GetDisplayNameAsync();
        dto.RunByUserId = _user.Id;
        dto.MayUsePaidModels = _user.MayUsePaidModels;
    }

    /// <summary>The catalogue, with paid models marked unavailable for users who may not use them.</summary>
    private IReadOnlyList<AvailableModel> ModelsForCurrentUser() =>
        _catalog.List()
            .Select(m => m.IsAvailable && m.Model.IsPaid && !_user.MayUsePaidModels
                ? m with { IsAvailable = false, UnavailableReason = "admins only on this instance" }
                : m)
            .ToList();

    private string DefaultModel(IReadOnlyList<AvailableModel> models) =>
        models.FirstOrDefault(m => m.IsAvailable && m.Model.Id == _catalog.DefaultModel)?.Model.Id
        ?? models.FirstOrDefault(m => m.IsAvailable)?.Model.Id
        ?? _catalog.DefaultModel;

    /// <summary>
    /// Category and tool must be in the admin-managed lists (the dropdowns only
    /// offer those, but a hand-made POST could send anything); null skips the check.
    /// </summary>
    private async Task ValidateListsAsync(string? category, string? aiTool, string? tags)
    {
        if (!string.IsNullOrEmpty(category) && !await _lookups.IsAllowedAsync(LookupKind.Category, category))
            ModelState.AddModelError(nameof(CreatePromptDto.Category), _t.Message("Pick a category from the list."));
        if (!string.IsNullOrEmpty(aiTool) && !await _lookups.IsAllowedAsync(LookupKind.Tool, aiTool))
            ModelState.AddModelError(nameof(CreatePromptDto.AiTool), _t.Message("Pick a tool from the list."));
        if (TagNames.Parse(tags).Error is { } tagError)
            ModelState.AddModelError(nameof(CreatePromptDto.Tags), _t.Message(tagError));
    }

    /// <summary>The dropdown lists, plus the prompt's current values when they were removed from the lists.</summary>
    private async Task FillListsAsync(string? currentCategory = null, string? currentTool = null)
    {
        var categories = await _lookups.NamesAsync(LookupKind.Category);
        var tools = await _lookups.NamesAsync(LookupKind.Tool);
        if (!string.IsNullOrEmpty(currentCategory) && !categories.Contains(currentCategory)) categories.Add(currentCategory);
        if (!string.IsNullOrEmpty(currentTool) && !tools.Contains(currentTool)) tools.Add(currentTool);
        ViewBag.Categories = categories;
        ViewBag.AiTools = tools;
    }

    /// <summary>Archived prompts are read-only: back to the prompt, with the reason.</summary>
    private IActionResult ArchivedRedirect(int id)
    {
        TempData["Error"] = "Archived prompts cannot be changed. Restore the prompt first.";
        return RedirectToAction(nameof(Details), new { id });
    }
}
