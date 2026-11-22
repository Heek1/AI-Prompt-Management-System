using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PromptManager.DTOs;
using PromptManager.Evals;
using PromptManager.Infrastructure;
using PromptManager.Llm;
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

    public EvalsController(IEvalService evals, IPromptService prompts, LlmCatalog catalog, ICurrentUser user)
    {
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
            CanManage = _user.CanManage(prompt.OwnerId)
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
            ModelState.AddModelError("", error!);
        }

        return View(CaseViewModel(prompt, dto));
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
        return run is null ? NotFound() : View(run);
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
