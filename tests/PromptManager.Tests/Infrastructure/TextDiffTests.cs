using PromptManager.Infrastructure;

namespace PromptManager.Tests.Infrastructure;

public class TextDiffTests
{
    private static string Text(DiffLine line) => string.Concat(line.Segments.Select(s => s.Text));

    [Fact]
    public void Identical_texts_have_no_changes()
    {
        var diff = TextDiff.Compare("a\nb", "a\nb");

        Assert.False(diff.HasChanges);
        Assert.All(diff.Rows, r => Assert.Equal(DiffLineKind.Unchanged, r.New.Kind));
    }

    [Fact]
    public void Line_endings_do_not_count_as_changes()
    {
        Assert.False(TextDiff.Compare("a\r\nb", "a\nb").HasChanges);
    }

    [Fact]
    public void Detects_added_and_removed_lines()
    {
        var diff = TextDiff.Compare("keep\nremove me\nend", "keep\nend\nadded");

        Assert.Equal(1, diff.Removed);
        Assert.Equal(1, diff.Added);
        Assert.Contains(diff.Rows, r => r.Old.Kind == DiffLineKind.Deleted && Text(r.Old) == "remove me" && r.New.Kind == DiffLineKind.Empty);
        Assert.Contains(diff.Rows, r => r.New.Kind == DiffLineKind.Inserted && Text(r.New) == "added");
    }

    [Fact]
    public void Modified_line_highlights_only_the_changed_words()
    {
        var diff = TextDiff.Compare("Summarise in three bullet points.", "Summarise in five bullet points.");

        var row = Assert.Single(diff.Rows);
        Assert.Equal(1, diff.Modified);
        Assert.Equal(DiffLineKind.Modified, row.New.Kind);
        Assert.Equal("Summarise in five bullet points.", Text(row.New));
        Assert.Equal("Summarise in three bullet points.", Text(row.Old));
        Assert.Equal("five", Assert.Single(row.New.Segments, s => s.Changed).Text);
        Assert.Equal("three", Assert.Single(row.Old.Segments, s => s.Changed).Text);
    }

    [Fact]
    public void Line_numbers_follow_each_side()
    {
        var diff = TextDiff.Compare("a\nb", "x\na\nb");

        var inserted = Assert.Single(diff.Rows, r => r.New.Kind == DiffLineKind.Inserted);
        Assert.Equal(1, inserted.New.Number);
        Assert.Null(inserted.Old.Number);
        Assert.Equal(3, diff.Rows.Last().New.Number);
        Assert.Equal(2, diff.Rows.Last().Old.Number);
    }
}
