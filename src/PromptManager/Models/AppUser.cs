using Microsoft.AspNetCore.Identity;

namespace PromptManager.Models;

public class AppUser : IdentityUser
{
    /// <summary>Shown as author, rater and runner; copied onto rows so history keeps the name it had.</summary>
    public string DisplayName { get; set; } = string.Empty;
}

public static class Roles
{
    public const string Admin = "Admin";
}
