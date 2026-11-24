using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Localization;

namespace PromptManager.Infrastructure;

/// <summary>
/// The account errors a person can actually hit on the sign-up form (taken
/// e-mail, weak password), in plain words and through the shared resource.
/// The others are about states the forms never create and keep the defaults.
/// </summary>
public sealed class LocalizedIdentityErrors(IStringLocalizer<SharedResource> t) : IdentityErrorDescriber
{
    private IdentityError Error(string code, string message) => new() { Code = code, Description = message };

    public override IdentityError DuplicateEmail(string email) =>
        Error(nameof(DuplicateEmail), t["An account with {0} already exists. Sign in instead.", email]);

    public override IdentityError DuplicateUserName(string userName) =>
        Error(nameof(DuplicateUserName), t["An account with {0} already exists. Sign in instead.", userName]);

    public override IdentityError InvalidEmail(string? email) =>
        Error(nameof(InvalidEmail), t["\"{0}\" is not a valid e-mail address.", email ?? ""]);

    public override IdentityError InvalidUserName(string? userName) =>
        Error(nameof(InvalidUserName), t["\"{0}\" is not a valid e-mail address.", userName ?? ""]);

    public override IdentityError PasswordTooShort(int length) =>
        Error(nameof(PasswordTooShort), t["The password needs at least {0} characters.", length]);

    public override IdentityError PasswordRequiresDigit() =>
        Error(nameof(PasswordRequiresDigit), t["The password needs a digit."]);

    public override IdentityError PasswordRequiresLower() =>
        Error(nameof(PasswordRequiresLower), t["The password needs a lower-case letter."]);

    public override IdentityError PasswordRequiresUpper() =>
        Error(nameof(PasswordRequiresUpper), t["The password needs an upper-case letter."]);

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) =>
        Error(nameof(PasswordRequiresUniqueChars), t["The password needs at least {0} different characters.", uniqueChars]);
}
