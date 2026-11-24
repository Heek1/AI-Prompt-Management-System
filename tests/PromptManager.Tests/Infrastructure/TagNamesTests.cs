using PromptManager.Infrastructure;

namespace PromptManager.Tests.Infrastructure;

public class TagNamesTests
{
    [Fact]
    public void Splits_on_commas_trims_and_collapses_spaces()
    {
        var (tags, error) = TagNames.Parse(" support ,  code   review,, email ");

        Assert.Null(error);
        Assert.Equal(new[] { "support", "code review", "email" }, tags.Select(t => t.Name));
        Assert.Equal(new[] { "support", "code-review", "email" }, tags.Select(t => t.Slug));
    }

    [Fact]
    public void Same_tag_in_another_case_is_kept_once_with_its_first_spelling()
    {
        var (tags, _) = TagNames.Parse("API, api, Api");

        Assert.Equal(("API", "api"), Assert.Single(tags));
    }

    [Fact]
    public void Ukrainian_tags_keep_their_letters()
    {
        var (tags, error) = TagNames.Parse("Переклад, служба підтримки");

        Assert.Null(error);
        Assert.Equal(new[] { "переклад", "служба-підтримки" }, tags.Select(t => t.Slug));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" , ,")]
    public void Empty_input_means_no_tags(string? input)
    {
        var (tags, error) = TagNames.Parse(input);

        Assert.Null(error);
        Assert.Empty(tags);
    }

    [Fact]
    public void Too_many_tags_is_an_error()
    {
        var input = string.Join(",", Enumerable.Range(1, TagNames.MaxTags + 1).Select(i => "t" + i));

        Assert.NotNull(TagNames.Parse(input).Error);
        Assert.Null(TagNames.Parse(string.Join(",", Enumerable.Range(1, TagNames.MaxTags).Select(i => "t" + i))).Error);
    }

    [Theory]
    [InlineData("!!!")]
    [InlineData("this tag is far longer than thirty characters")]
    public void Unusable_tag_is_an_error(string input)
    {
        Assert.NotNull(TagNames.Parse(input).Error);
    }
}
