using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PromptManager.Models;
using PromptManager.Services;

namespace PromptManager.Controllers;

[Authorize(Roles = Roles.Admin)]
public class CostsController : Controller
{
    private readonly ICostService _costs;

    public CostsController(ICostService costs)
    {
        _costs = costs;
    }

    // GET /Costs
    public async Task<IActionResult> Index() => View(await _costs.SummaryAsync());
}
