using PromptManager.Infrastructure;

namespace PromptManager.Tests.Infrastructure;

public class PromptTemplateTests
{
    [Fact]
    public void Extracts_distinct_names_in_order_of_appearance()
    {
        var names = PromptTemplate.ExtractVariables("Translate {{text}} into {{ language }}. Keep {{text}} short. {{_x1}}");

        Assert.Equal(new[] { "text", "language", "_x1" }, names);
    }

    [Theory]
    [InlineData("{\"json\": {\"nested\": 1}}")]   // JSON braces
    [InlineData("{{ not a name }}")]               // spaces inside the name
    [InlineData("{{1abc}}")]                       // starts with a digit
    [InlineData("{single}")]
    public void Ignores_text_that_is_not_a_placeholder(string text)
    {
        Assert.Empty(PromptTemplate.ExtractVariables(text));
        Assert.Equal(text, PromptTemplate.Render(text, new Dictionary<string, string>()).Text);
    }

    [Fact]
    public void Renders_all_placeholders_including_repeats()
    {
        var (text, missing) = PromptTemplate.Render(
            "Hi {{name}}, {{ name }} again, in {{lang}}.",
            new Dictionary<string, string> { ["name"] = "Ann", ["lang"] = "Ukrainian" });

        Assert.Equal("Hi Ann, Ann again, in Ukrainian.", text);
        Assert.Empty(missing);
    }

    [Fact]
    public void Reports_missing_values_once_and_leaves_them_in_place()
    {
        var (text, missing) = PromptTemplate.Render("{{a}} {{b}} {{a}}", new Dictionary<string, string> { ["b"] = "B" });

        Assert.Equal(new[] { "a" }, missing);
        Assert.Equal("{{a}} B {{a}}", text);
    }

    [Fact]
    public void Does_not_expand_placeholders_inside_values()
    {
        var (text, _) = PromptTemplate.Render("{{a}}", new Dictionary<string, string> { ["a"] = "{{b}}", ["b"] = "nope" });

        Assert.Equal("{{b}}", text);
    }

    [Fact]
    public void Empty_value_counts_as_provided()
    {
        var (text, missing) = PromptTemplate.Render("[{{a}}]", new Dictionary<string, string> { ["a"] = "" });

        Assert.Equal("[]", text);
        Assert.Empty(missing);
    }
}
