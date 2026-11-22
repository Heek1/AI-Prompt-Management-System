using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using PromptManager.Llm;
using PromptManager.Models;

namespace PromptManager.Services;

/// <summary>Bound from the "Auth" configuration section.</summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>Accounts registered with these e-mail addresses become admins.</summary>
    public List<string> AdminEmails { get; set; } = [];

    public bool IsAdminEmail(string? email) =>
        email is not null && AdminEmails.Any(a => string.Equals(a.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>The signed-in user and what they may do, in one place for controllers and views.</summary>
public interface ICurrentUser
{
    bool IsSignedIn { get; }
    string? Id { get; }
    bool IsAdmin { get; }

    /// <summary>Display name, loaded once per request.</summary>
    Task<string> GetDisplayNameAsync();

    /// <summary>Owners and admins may edit, version, archive and delete a prompt. Prompts without an owner are admin-only.</summary>
    bool CanManage(string? ownerId);

    /// <summary>Whether models that call a paid provider are open to this user (Llm:PaidModelsAccess).</summary>
    bool MayUsePaidModels { get; }
}

public sealed class CurrentUser(
    IHttpContextAccessor http,
    UserManager<AppUser> users,
    IOptions<LlmOptions> llmOptions) : ICurrentUser
{
    private string? _displayName;

    private ClaimsPrincipal Principal => http.HttpContext?.User ?? new ClaimsPrincipal();

    public bool IsSignedIn => Principal.Identity?.IsAuthenticated == true;

    public string? Id => IsSignedIn ? users.GetUserId(Principal) : null;

    public bool IsAdmin => IsSignedIn && Principal.IsInRole(Roles.Admin);

    public async Task<string> GetDisplayNameAsync()
    {
        if (_displayName is not null) return _displayName;
        if (!IsSignedIn) return _displayName = "anonymous";

        // The cookie can outlive its account until the next security-stamp
        // check; fall back to the name stored in the cookie.
        var user = await users.GetUserAsync(Principal);
        return _displayName = !string.IsNullOrWhiteSpace(user?.DisplayName) ? user.DisplayName
            : user?.Email ?? Principal.Identity?.Name ?? "user";
    }

    public bool CanManage(string? ownerId) =>
        IsAdmin || (IsSignedIn && ownerId is not null && ownerId == Id);

    public bool MayUsePaidModels =>
        IsAdmin || (IsSignedIn && string.Equals(llmOptions.Value.PaidModelsAccess, PaidAccess.Everyone, StringComparison.OrdinalIgnoreCase));
}
