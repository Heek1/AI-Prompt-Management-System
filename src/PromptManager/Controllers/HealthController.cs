using Microsoft.AspNetCore.Mvc;
using PromptManager.Services;

namespace PromptManager.Controllers;

[Route("health")]
public class HealthController : Controller
{
    private readonly IPromptService _service;
    private readonly IWebHostEnvironment _env;

    public HealthController(IPromptService service, IWebHostEnvironment env)
    {
        _service = service;
        _env = env;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var (totalPrompts, totalUsages) = await _service.GetHealthStatsAsync();

        return Ok(new
        {
            ApplicationName = "AI Prompt Manager",
            Environment = _env.EnvironmentName,
            UtcTime = DateTime.UtcNow,
            TotalPrompts = totalPrompts,
            TotalUsages = totalUsages
        });
    }
}
