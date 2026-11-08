using Microsoft.AspNetCore.Mvc;

namespace AI_Prompt_Management_System.Controllers;

public class HomeController : Controller
{
    public IActionResult Index() => RedirectToAction("Index", "Prompts");
}
