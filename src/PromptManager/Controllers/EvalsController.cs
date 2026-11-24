using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PromptManager.DTOs;
using PromptManager.Evals;
using PromptManager.Infrastructure;
using PromptManager.Llm;
using PromptManager.Models;
using PromptManager.Services;
using PromptManager.ViewModels;

namespace PromptManager.Controllers;

/// <summary>
/// Eval cases belong to a prompt and are edited by its author or an admin.
/// Any signed-in user may run an eval; results are visible to everyone.
/// </summary>
public class EvalsController : Controller
{
    private readonly IEvalService _evals;
    private readonly IPromptService _prompts;
    private readonly LlmCatalog _catalog;
    private readonly ICurrentUser _user;
    private readonly ImprovementAssistant _assistant;
    private readonly ICollaborationService _collab;
    private readonly Microsoft.Extensions.Localization.IStringLocalizer<PromptManager.SharedResource> _t;

    public EvalsController(IEvalService evals, IPromptService prompts, LlmCatalog catalog, ICurrentUser user,
        ImprovementAssistant assistant, ICollaborationService collab, Microsoft.Extensions.Localization.IStringLocalizer<PromptManager.SharedResource> t)
    {
        _t = t;
        _collab = collab;
        _assistant = assistant;
        _evals = evals;
        _prompts = prompts;
        _catalog = catalog;
        _user = user;
    }

    // GET /Evals/Index/5  (5 = prompt id)
    public async Task<IActionResult> Index(int id)
    {
        var prompt = await _prompts.GetByIdAsync(id);
        if (prompt == null) return NotFound();

        var models = _catalog.List()
            .Select(m => m.IsAvailable && m.Model.IsPaid && !_user.MayUsePaidModels
                ? m with { IsAvailable = false, UnavailableReason = "admins only on this instance" }
                : m)
            .ToList();

        return View(new EvalsIndexViewModel
        {
            Prompt = prompt,
            Cases = await _evals.ListCasesAsync(id),
            Runs = await _evals.ListRunsAsync(id),
            Models = models,
            DefaultModel = models.FirstOrDefault(m => m.IsAvailable && m.Model.Id == _catalog.DefaultModel)?.Model.Id
                ?? models.FirstOrDefault(m => m.IsAvailable)?.Model.Id ?? "",
            IsSignedIn = _user.IsSignedIn,
            CanManage = _user.CanManage(prompt.OwnerId),
            Variables = prompt.Versions
                .SelectMany(v => PromptTemplate.ExtractVariables(v.PromptText))
                .Distinct(StringComparer.Ordinal)
                .ToList()
        });
    }

    // GET /Evals/Case/5?caseId=3  (new case when caseId is empty)
    [Authorize]
    public async Task<IActionResult> Case(int id, int? caseId)
    {
        var prompt = await _prompts.GetByIdAsync(id);
        if (prompt == null) return NotFound();
        if (!_user.CanManage(prompt.OwnerId)) return Forbid();

        var dto = caseId is { } cid ? await _evals.GetCaseAsync(id, cid) : new EvalCaseDto { PromptId = id };
        if (dto == null) return NotFound();
        return View(CaseViewModel(prompt, dto));
    }

    // POST /Evals/Case
    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Case(EvalCaseDto dto)
    {
        var prompt = await _prompts.GetByIdAsync(dto.PromptId);
        if (prompt == null) return NotFound();
        if (!_user.CanManage(prompt.OwnerId)) return Forbid();

        if (ModelState.IsValid)
        {
            var (savedId, error) = await _evals.SaveCaseAsync(dto);
            if (savedId is not null)
            {
                TempData["Success"] = "Case saved.";
                return RedirectToAction(nameof(Index), new { id = dto.PromptId });
            }
            ModelState.AddModelError("", _t.Message(error));
        }

        return View(CaseViewModel(prompt, dto));
    }

    // POST /Evals/AddInjectionPack
    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddInjectionPack(int promptId, string variable)
    {
        var prompt = await _prompts.GetByIdAsync(promptId);
        if (prompt == null) return NotFound();
        if (!_user.CanManage(prompt.OwnerId)) return Forbid();

        var (added, error) = await _evals.AddInjectionPackAsync(promptId, variable ?? "");
        if (error is null)
            TempData["Success"] = _t["Added {0} prompt-injection cases attacking through {1}. Run an eval to see how the prompt holds up.", added, "{{" + variable + "}}"].Value;
        else
            TempData["Error"] = error;
        return RedirectToAction(nameof(Index), new { id = promptId });
    }

    // POST /Evals/DeleteCase
    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCase(int promptId, int caseId)
    {
        var prompt = await _prompts.GetByIdAsync(promptId);
        if (prompt == null) return NotFound();
        if (!_user.CanManage(prompt.OwnerId)) return Forbid();

        if (await _evals.DeleteCaseAsync(promptId, caseId))
            TempData["Success"] = "Case deleted. Results of earlier evals are kept.";
        else
            TempData["Error"] = "Case not found.";
        return RedirectToAction(nameof(Index), new { id = promptId });
    }

    // POST /Evals/Start
    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Start(StartEvalDto dto)
    {
        if (!ModelState.IsValid)
        {
            TempData["Error"] = "Choose a model.";
            return RedirectToAction(nameof(Index), new { id = dto.PromptId });
        }

        dto.RunBy = await _user.GetDisplayNameAsync();
        dto.RunByUserId = _user.Id;
        dto.MayUsePaidModels = _user.MayUsePaidModels;

        var (evalId, error) = await _evals.StartAsync(dto);
        if (evalId is null)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Index), new { id = dto.PromptId });
        }
        return RedirectToAction(nameof(Run), new { id = evalId });
    }

    // POST /Evals/CompareModels
    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CompareModels(CompareModelsDto dto)
    {
        dto.RunBy = await _user.GetDisplayNameAsync();
        dto.RunByUserId = _user.Id;
        dto.MayUsePaidModels = _user.MayUsePaidModels;

        var (ids, error) = await _evals.StartComparisonAsync(dto);
        if (ids is null)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Index), new { id = dto.PromptId });
        }
        return RedirectToAction(nameof(Compare), new { ids = string.Join(",", ids) });
    }

    // GET /Evals/Compare?ids=7,8,9
    public async Task<IActionResult> Compare(string ids)
    {
        var parsed = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out var n) ? n : 0)
            .Where(n => n > 0)
            .Distinct()
            .Take(EvalService.MaxModelsPerComparison)
            .ToList();
        if (parsed.Count == 0) return NotFound();

        var rows = await _evals.CompareAsync(parsed);
        if (rows.Count == 0) return NotFound();

        // All rows must belong to one prompt and version, or the table would mislead.
        var first = await _evals.GetRunAsync(rows[0].Eval.Id);
        if (rows.Any(r => r.Eval.PromptId != first!.PromptId || r.Eval.VersionNumber != first.VersionNumber)) return BadRequest();

        return View(new ModelComparisonViewModel { First = first!, Rows = rows });
    }

    // GET /Evals/Run/7
    public async Task<IActionResult> Run(int id)
    {
        var run = await _evals.GetRunAsync(id);
        if (run is null) return NotFound();

        var prompt = await _prompts.GetByIdAsync(run.PromptId);
        if (prompt is not null && !prompt.IsArchived && _user.CanManage(prompt.OwnerId)
            && run.Status == EvalRunStatus.Completed && run.PassedCases < run.TotalCases)
        {
            // The echo model cannot write a prompt, so it is not offered.
            ViewData["SuggestModels"] = _catalog.List()
                .Where(m => !string.Equals(m.Model.Provider, "Echo", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        return View(run);
    }

    // POST /Evals/Suggest: asks a model for a revision (B11). The answer is
    // stored as a run and the review page reads it from there, so reloading
    // that page does not call (and pay for) the model again.
    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Suggest(int evalRunId, string model, CancellationToken ct)
    {
        if (await ManagedPromptOfEvalAsync(evalRunId) is null) return Forbid();

        var (suggestion, error) = await _assistant.SuggestAsync(evalRunId, model, _user.MayUsePaidModels,
            await _user.GetDisplayNameAsync(), _user.Id, ct);
        if (suggestion is null)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Run), new { id = evalRunId });
        }
        return RedirectToAction(nameof(Suggestion), new { id = suggestion.RunId, evalRunId });
    }

    // GET /Evals/Suggestion/12?evalRunId=7  (12 = the run that holds the answer)
    [Authorize]
    public async Task<IActionResult> Suggestion(int id, int evalRunId)
    {
        if (await ManagedPromptOfEvalAsync(evalRunId) is not { } prompt) return Forbid();
        var eval = await _evals.GetRunAsync(evalRunId);
        var suggestion = await _assistant.LoadAsync(id, evalRunId);
        if (eval is null || suggestion is null) return NotFound();

        return View(new SuggestionViewModel
        {
            Prompt = prompt,
            Eval = eval,
            Suggestion = suggestion,
            Diff = TextDiff.Compare(suggestion.BaseText, suggestion.RevisedText)
        });
    }

    // POST /Evals/AcceptSuggestion: saves the (possibly edited) text as a draft
    // and runs the same eval on it, with the same model and judge.
    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AcceptSuggestion(int evalRunId, string? text, string? changeNote)
    {
        if (await ManagedPromptOfEvalAsync(evalRunId) is not { } prompt) return Forbid();
        var eval = await _evals.GetRunAsync(evalRunId);
        if (eval is null) return NotFound();

        if (string.IsNullOrWhiteSpace(text) || text.Length is < FieldLimits.PromptTextMin or > FieldLimits.PromptText)
        {
            TempData["Error"] = _t["The text must be between {0} and {1} characters.", FieldLimits.PromptTextMin, FieldLimits.PromptText].Value;
            return RedirectToAction(nameof(Run), new { id = evalRunId });
        }

        var author = await _user.GetDisplayNameAsync();
        var note = string.IsNullOrWhiteSpace(changeNote) ? $"Suggested from eval #{evalRunId}" : changeNote.Trim();
        var saved = await _prompts.CreateVersionAsync(new CreatePromptVersionDto
        {
            PromptId = prompt.Id,
            PromptText = text,
            ChangeNote = note.Length > FieldLimits.ChangeNote ? note[..FieldLimits.ChangeNote] : note,
            CreatedBy = author,
            CreatedById = _user.Id
        });
        if (!saved)
        {
            TempData["Error"] = "Could not save the version.";
            return RedirectToAction("Details", "Prompts", new { id = prompt.Id });
        }

        var version = (await _prompts.GetByIdAsync(prompt.Id))!.CurrentVersion;
        await _collab.NotifyWatchersAsync(prompt.Id, $"{author} added v{version} of {prompt.Title}, suggested from eval #{evalRunId}.",
            $"/Prompts/Details/{prompt.Id}#v{version}");
        var (newEvalId, error) = await _evals.StartAsync(new StartEvalDto
        {
            PromptId = prompt.Id,
            VersionNumber = version,
            Model = eval.Model,
            JudgeModel = eval.JudgeModel,
            RunBy = author,
            RunByUserId = _user.Id,
            MayUsePaidModels = _user.MayUsePaidModels
        });
        if (newEvalId is null)
        {
            TempData["Error"] = _t["Saved v{0} as a draft, but the eval did not start: {1}", version, error ?? ""].Value;
            return RedirectToAction("Details", "Prompts", new { id = prompt.Id });
        }
        TempData["Success"] = _t["Saved v{0} as a draft and started the same eval on it.", version].Value;
        return RedirectToAction(nameof(Run), new { id = newEvalId });
    }

    /// <summary>The prompt an eval belongs to, when the current user may change it.</summary>
    private async Task<PromptDetailsDto?> ManagedPromptOfEvalAsync(int evalRunId)
    {
        var eval = await _evals.GetRunAsync(evalRunId);
        if (eval is null) return null;
        var prompt = await _prompts.GetByIdAsync(eval.PromptId);
        return prompt is not null && !prompt.IsArchived && _user.CanManage(prompt.OwnerId) ? prompt : null;
    }

    private static EvalCaseViewModel CaseViewModel(PromptDetailsDto prompt, EvalCaseDto dto) => new()
    {
        Prompt = prompt,
        Case = dto,
        Variables = prompt.Versions
            .SelectMany(v => PromptTemplate.ExtractVariables(v.PromptText))
            .Distinct(StringComparer.Ordinal)
            .ToList()
    };
}
