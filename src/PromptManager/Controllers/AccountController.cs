using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PromptManager.Models;
using PromptManager.Services;
using PromptManager.ViewModels;

namespace PromptManager.Controllers;

public class AccountController : Controller
{
    private readonly UserManager<AppUser> _users;
    private readonly SignInManager<AppUser> _signIn;
    private readonly AuthOptions _auth;
    private readonly ILogger<AccountController> _logger;

    public AccountController(UserManager<AppUser> users, SignInManager<AppUser> signIn, IOptions<AuthOptions> auth, ILogger<AccountController> logger)
    {
        _users = users;
        _signIn = signIn;
        _auth = auth.Value;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Register(string? returnUrl) => View(new RegisterViewModel { ReturnUrl = returnUrl });

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = new AppUser
        {
            UserName = model.Email.Trim(),
            Email = model.Email.Trim(),
            DisplayName = model.DisplayName.Trim()
        };
        var result = await _users.CreateAsync(user, model.Password);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError(string.Empty, error.Description);
            return View(model);
        }

        // Admins are chosen in configuration, so nobody can make themselves one.
        if (_auth.IsAdminEmail(user.Email))
        {
            await _users.AddToRoleAsync(user, Roles.Admin);
            _logger.LogInformation("Account {UserId} registered as admin.", user.Id);
        }

        await _signIn.SignInAsync(user, isPersistent: false);
        return LocalRedirect(SafeReturnUrl(model.ReturnUrl));
    }

    [HttpGet]
    public IActionResult Login(string? returnUrl) => View(new LoginViewModel { ReturnUrl = returnUrl });

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var result = await _signIn.PasswordSignInAsync(model.Email.Trim(), model.Password, model.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded)
            return LocalRedirect(SafeReturnUrl(model.ReturnUrl));

        // The same message for unknown e-mail and wrong password, so the form
        // cannot be used to find out which addresses have accounts.
        ModelState.AddModelError(string.Empty, result.IsLockedOut
            ? "Too many failed attempts. Try again in 15 minutes."
            : "Invalid e-mail or password.");
        return View(model);
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await _signIn.SignOutAsync();
        return RedirectToAction("Index", "Prompts");
    }

    [HttpGet]
    public IActionResult AccessDenied()
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return View();
    }

    // Only local URLs, so the login form cannot be used to send people to another site.
    private string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Action("Index", "Prompts")!;
}
