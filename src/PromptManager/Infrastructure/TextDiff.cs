using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace PromptManager.Infrastructure;

public enum DiffLineKind { Unchanged, Deleted, Inserted, Modified, Empty }

public sealed record DiffSegment(string Text, bool Changed);

public sealed record DiffLine(DiffLineKind Kind, int? Number, IReadOnlyList<DiffSegment> Segments);

public sealed record DiffRow(DiffLine Old, DiffLine New);

public sealed record DiffResult(IReadOnlyList<DiffRow> Rows, int Added, int Removed, int Modified)
{
    public bool HasChanges => Added + Removed + Modified > 0;
}

/// <summary>
/// Side-by-side line diff with word-level highlights inside modified lines.
/// Wraps DiffPlex so views and tests depend on this small model only.
/// </summary>
public static class TextDiff
{
    public static DiffResult Compare(string oldText, string newText)
    {
        var model = SideBySideDiffBuilder.Diff(Normalize(oldText), Normalize(newText), ignoreWhiteSpace: false, ignoreCase: false);

        var rows = model.OldText.Lines
            .Zip(model.NewText.Lines, (o, n) => new DiffRow(ToLine(o), ToLine(n)))
            .ToList();

        return new DiffResult(
            rows,
            Added: rows.Count(r => r.New.Kind == DiffLineKind.Inserted),
            Removed: rows.Count(r => r.Old.Kind == DiffLineKind.Deleted),
            Modified: rows.Count(r => r.New.Kind == DiffLineKind.Modified));
    }

    // Line endings must not count as changes when versions were typed on different systems.
    private static string Normalize(string s) => s.Replace("\r\n", "\n");

    private static DiffLine ToLine(DiffPiece piece)
    {
        var kind = piece.Type switch
        {
            ChangeType.Deleted => DiffLineKind.Deleted,
            ChangeType.Inserted => DiffLineKind.Inserted,
            ChangeType.Modified => DiffLineKind.Modified,
            ChangeType.Imaginary => DiffLineKind.Empty,
            _ => DiffLineKind.Unchanged
        };

        IReadOnlyList<DiffSegment> segments = kind == DiffLineKind.Modified && piece.SubPieces.Count > 0
            ? piece.SubPieces
                .Where(p => p.Type != ChangeType.Imaginary)
                .Select(p => new DiffSegment(p.Text ?? "", p.Type != ChangeType.Unchanged))
                .ToList()
            : [new DiffSegment(piece.Text ?? "", kind is DiffLineKind.Deleted or DiffLineKind.Inserted)];

        return new DiffLine(kind, piece.Position, segments);
    }
}
