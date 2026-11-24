using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;

namespace PromptManager.Controllers;

/// <summary>The interface language (C8), kept in the standard culture cookie for a year.</summary>
public class LanguageController : Controller
{
    /// <summary>Interface languages; formats (dates, numbers) stay en-GB in both.</summary>
    public static readonly string[] Supported = ["en", "uk"];

    // POST /Language
    [HttpPost("/Language")]
    [ValidateAntiForgeryToken]
    public IActionResult Set(string ui, string? returnUrl)
    {
        if (Supported.Contains(ui))
        {
            Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture("en-GB", ui)),
                new CookieOptions
                {
                    Expires = DateTimeOffset.UtcNow.AddYears(1),
                    IsEssential = true,
                    SameSite = SameSiteMode.Lax,
                    HttpOnly = true,
                    // Behind Render's proxy every request is https; plain http
                    // happens only on a developer's machine and in tests.
                    Secure = Request.IsHttps
                });
        }
        // Only back to a page of this app, never to an address from the form.
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }
}
