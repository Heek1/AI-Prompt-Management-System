using Microsoft.AspNetCore.Mvc;
using AI_Prompt_Management_System.Services;
using AI_Prompt_Management_System.ViewModels;

namespace AI_Prompt_Management_System.Controllers;

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
