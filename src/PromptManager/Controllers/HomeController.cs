using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace PromptManager.Controllers;

public class HomeController : Controller
{
    public IActionResult Index() => RedirectToAction("Index", "Prompts");

    // Target of UseExceptionHandler and UseStatusCodePagesWithReExecute outside Development.
    [Route("/Home/Error/{statusCode?}")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error(int? statusCode)
    {
        var code = statusCode ?? StatusCodes.Status500InternalServerError;
        Response.StatusCode = code;

        ViewData["StatusCode"] = code;
        ViewData["RequestId"] = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
        return View();
    }
}
