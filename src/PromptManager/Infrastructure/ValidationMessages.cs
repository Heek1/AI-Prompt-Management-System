using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace PromptManager.Infrastructure;

/// <summary>
/// Gives every validation attribute without its own message a plain English
/// one from the shared resource, so it is translated like the rest. Without
/// this, attributes such as the implicit [Required] MVC adds to non-nullable
/// fields use the framework's built-in English text ("The X field is
/// required."), which the localizer never sees.
/// </summary>
public sealed class DefaultValidationMessages : IValidationMetadataProvider
{
    public void CreateValidationMetadata(ValidationMetadataProviderContext context)
    {
        foreach (var attribute in context.ValidationMetadata.ValidatorMetadata.OfType<ValidationAttribute>())
        {
            if (attribute.ErrorMessage is not null || attribute.ErrorMessageResourceName is not null)
                continue;

            attribute.ErrorMessage = attribute switch
            {
                RequiredAttribute => "Fill in {0}.",
                StringLengthAttribute { MinimumLength: > 0 } => "{0} must be between {2} and {1} characters.",
                StringLengthAttribute => "{0} can have at most {1} characters.",
                MaxLengthAttribute => "{0} can have at most {1} characters.",
                MinLengthAttribute => "{0} needs at least {1} characters.",
                RangeAttribute => "{0} must be between {1} and {2}.",
                CompareAttribute => "{0} and {1} do not match.",
                EmailAddressAttribute => "{0} is not a valid e-mail address.",
                _ => null
            };
        }
    }
}

/// <summary>Model-binding messages (a value that cannot be read at all) through the shared resource.</summary>
public sealed class LocalizedBindingMessages(IStringLocalizerFactory factory) : IConfigureOptions<MvcOptions>
{
    public void Configure(MvcOptions options)
    {
        var t = factory.Create(typeof(SharedResource));
        var m = options.ModelBindingMessageProvider;
        m.SetAttemptedValueIsInvalidAccessor((value, field) => t["The value \"{0}\" is not valid for {1}.", value, field]);
        m.SetMissingBindRequiredValueAccessor(field => t["Fill in {0}.", field]);
        m.SetMissingKeyOrValueAccessor(() => t["A value is required."]);
        m.SetMissingRequestBodyRequiredValueAccessor(() => t["A value is required."]);
        m.SetNonPropertyAttemptedValueIsInvalidAccessor(value => t["The value \"{0}\" is not valid.", value]);
        m.SetNonPropertyUnknownValueIsInvalidAccessor(() => t["The value is not valid."]);
        m.SetUnknownValueIsInvalidAccessor(field => t["The value is not valid for {0}.", field]);
        m.SetValueIsInvalidAccessor(value => t["The value \"{0}\" is not valid.", value]);
        m.SetValueMustBeANumberAccessor(field => t["{0} must be a number.", field]);
        m.SetValueMustNotBeNullAccessor(field => t["Fill in {0}.", field]);
        m.SetNonPropertyValueMustBeANumberAccessor(() => t["The value must be a number."]);
    }
}
