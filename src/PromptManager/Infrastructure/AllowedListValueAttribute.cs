using System.ComponentModel.DataAnnotations;

namespace PromptManager.Infrastructure;

/// <summary>
/// Rejects values that are not in one of the fixed lists from Constants.cs.
/// The dropdowns only offer those values, but a hand-made POST could send anything.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AllowedListValueAttribute : ValidationAttribute
{
    private readonly IReadOnlyCollection<string> _allowed;

    public AllowedListValueAttribute(AllowedList list)
    {
        _allowed = list switch
        {
            AllowedList.Categories => PromptCategories.All,
            AllowedList.AiTools => AiTools.All,
            _ => throw new ArgumentOutOfRangeException(nameof(list))
        };
    }

    // Empty values are left to [Required].
    public override bool IsValid(object? value) =>
        value is not string s || string.IsNullOrEmpty(s) || _allowed.Contains(s);

    public override string FormatErrorMessage(string name) =>
        ErrorMessage ?? $"{name} must be one of: {string.Join(", ", _allowed)}.";
}

public enum AllowedList
{
    Categories,
    AiTools
}
