using Microsoft.AspNetCore.Mvc;

namespace PromptManager.Controllers;

public class HomeController : Controller
{
    public IActionResult Index() => RedirectToAction("Index", "Prompts");
}
