using System.ComponentModel.DataAnnotations;
using PromptManager.DTOs;
using PromptManager.Infrastructure;

namespace PromptManager.Tests.Validation;

public class DtoValidationTests
{
    private static List<string> Errors(object dto)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(dto, new ValidationContext(dto), results, validateAllProperties: true);
        return results.SelectMany(r => r.MemberNames).ToList();
    }

    private static CreatePromptDto ValidPrompt() => new()
    {
        Title = "Title",
        PromptText = new string('x', FieldLimits.PromptTextMin),
        Category = PromptCategories.All[0],
        AiTool = AiTools.All[0],
        Author = "alice"
    };

    [Fact]
    public void Valid_prompt_has_no_errors()
    {
        Assert.Empty(Errors(ValidPrompt()));
    }

    [Fact]
    public void Category_outside_the_list_is_rejected()
    {
        var dto = ValidPrompt();
        dto.Category = "Poetry";

        Assert.Equal(new[] { nameof(CreatePromptDto.Category) }, Errors(dto));
    }

    [Fact]
    public void Ai_tool_outside_the_list_is_rejected()
    {
        var dto = ValidPrompt();
        dto.AiTool = "SomeOtherTool";

        Assert.Equal(new[] { nameof(CreatePromptDto.AiTool) }, Errors(dto));
    }

    [Theory]
    [InlineData(nameof(CreatePromptDto.Title), FieldLimits.Title)]
    [InlineData(nameof(CreatePromptDto.Description), FieldLimits.Description)]
    [InlineData(nameof(CreatePromptDto.Author), FieldLimits.PersonName)]
    [InlineData(nameof(CreatePromptDto.PromptText), FieldLimits.PromptText)]
    public void Create_prompt_fields_over_the_limit_are_rejected(string property, int limit)
    {
        var dto = ValidPrompt();
        typeof(CreatePromptDto).GetProperty(property)!.SetValue(dto, new string('x', limit + 1));

        Assert.Equal(new[] { property }, Errors(dto));
    }

    [Fact]
    public void Prompt_text_below_the_minimum_is_rejected()
    {
        var dto = ValidPrompt();
        dto.PromptText = new string('x', FieldLimits.PromptTextMin - 1);

        Assert.Equal(new[] { nameof(CreatePromptDto.PromptText) }, Errors(dto));
    }

    [Fact]
    public void Version_change_note_and_author_have_limits()
    {
        var dto = new CreatePromptVersionDto
        {
            PromptId = 1,
            PromptText = new string('x', FieldLimits.PromptTextMin),
            ChangeNote = new string('x', FieldLimits.ChangeNote + 1),
            CreatedBy = new string('x', FieldLimits.PersonName + 1)
        };

        Assert.Equal(
            new[] { nameof(CreatePromptVersionDto.ChangeNote), nameof(CreatePromptVersionDto.CreatedBy) }.Order(),
            Errors(dto).Order());
    }

    [Fact]
    public void Rating_user_name_comment_and_range_are_validated()
    {
        var dto = new RatePromptDto
        {
            PromptId = 1,
            UserName = new string('x', FieldLimits.PersonName + 1),
            Rating = 6,
            Comment = new string('x', FieldLimits.Comment + 1)
        };

        Assert.Equal(
            new[] { nameof(RatePromptDto.UserName), nameof(RatePromptDto.Rating), nameof(RatePromptDto.Comment) }.Order(),
            Errors(dto).Order());
    }
}
