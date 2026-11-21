using Microsoft.AspNetCore.Mvc;
using PromptManager.Services;
using PromptManager.ViewModels;

namespace PromptManager.Controllers;

public class HistoryController : Controller
{
    private readonly IPromptService _service;

    public HistoryController(IPromptService service)
    {
        _service = service;
    }

    // GET /History
    public async Task<IActionResult> Index()
    {
        var usages = await _service.GetUsageHistoryAsync();
        var vm = new HistoryIndexViewModel
        {
            Usages = usages,
            TotalCount = usages.Count
        };
        return View(vm);
    }
}
