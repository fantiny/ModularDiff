using ModularDiff.Contracts;

namespace ModularDiff.Presentation.SideBySide;

public sealed class SideBySidePresentation : ITextPresentation
{
    public string Id => "side-by-side";
    public string DisplayName => "Side by side · aligned blocks";

    public TextPresentation Build(IReadOnlyList<DiffLine> lines, int contextLines = -1, CancellationToken cancellationToken = default)
    {
        if (contextLines < -1) throw new ArgumentOutOfRangeException(nameof(contextLines));
        var rows = new List<AlignedRow>(); var hunks = new List<DiffHunk>();
        TextCell? Cell(DiffLine? line, bool left, IReadOnlyList<HighlightSpan>? spans = null)
        {
            int? number = left ? line?.LeftLine : line?.RightLine;
            return number is null ? null : new(number.Value, (left ? line!.LeftText : line!.RightText) ?? "",
                (left ? line!.LeftEnding : line!.RightEnding) ?? "EOF", spans ?? []);
        }
        for (int index = 0; index < lines.Count;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (lines[index].Kind == EditKind.Equal)
            {
                var line = lines[index++]; rows.Add(new(rows.Count, Cell(line, true), Cell(line, false))); continue;
            }
            var deletes = new List<DiffLine>(); var inserts = new List<DiffLine>();
            while (index < lines.Count && lines[index].Kind != EditKind.Equal)
            {
                if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                var line = lines[index++];
                (line.Kind == EditKind.Delete ? deletes : inserts).Add(line);
            }
            int start = rows.Count, id = hunks.Count;
            // Similarity alignment prevents a newly inserted method from shifting every replacement.
            // This is display alignment only; the algorithm's edit script is never rewritten.
            foreach (var (a, b) in Align(deletes, inserts, cancellationToken))
            {
                var (leftSpans, rightSpans) = Highlight(a?.LeftText, b?.RightText);
                rows.Add(new(rows.Count, Cell(a, true, leftSpans), Cell(b, false, rightSpans), id));
            }
            hunks.Add(new(id, start, rows.Count - 1));
        }
        if (contextLines == -1) return new(rows, hunks, rows.Count);
        var visible = new bool[rows.Count];
        foreach (var hunk in hunks)
            for (int i = Math.Max(0, hunk.StartRow - contextLines); i <= Math.Min(rows.Count - 1, hunk.EndRow + contextLines); i++) visible[i] = true;
        var filtered = new List<AlignedRow>();
        for (int i = 0; i < rows.Count;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (visible[i]) { filtered.Add(rows[i++]); continue; }
            int begin = i; while (i < rows.Count && !visible[i]) i++;
            filtered.Add(new(begin, null, null, HiddenLines: i - begin));
        }
        return new(filtered, hunks, rows.Count);
    }

    private static IReadOnlyList<(DiffLine? Left, DiffLine? Right)> Align(List<DiffLine> left, List<DiffLine> right, CancellationToken token)
    {
        var pairs = new List<(DiffLine?, DiffLine?)>();
        int n = left.Count, m = right.Count;
        if ((long)n * m > 20_000 || left.Any(l => l.LeftText?.Length > 4096) || right.Any(l => l.RightText?.Length > 4096))
        {
            for (int k = 0; k < Math.Max(n, m); k++) pairs.Add((k < n ? left[k] : null, k < m ? right[k] : null));
            return pairs;
        }
        var a = left.Select(l => (l.LeftText ?? "").Trim()).ToArray();
        var b = right.Select(l => (l.RightText ?? "").Trim()).ToArray();
        static double Cost(string x, string y)
        {
            if (x == y) return 0;
            int prefix = 0, suffix = 0;
            while (prefix < Math.Min(x.Length, y.Length) && x[prefix] == y[prefix]) prefix++;
            while (suffix < Math.Min(x.Length, y.Length) - prefix && x[^(suffix + 1)] == y[^(suffix + 1)]) suffix++;
            double similarity = (double)(prefix + suffix) / Math.Max(1, Math.Max(x.Length, y.Length));
            return similarity < .15 ? 1.2 : 1 - similarity;
        }
        const double gap = .55;
        var costs = new double[n + 1, m + 1]; var actions = new byte[n + 1, m + 1];
        for (int i = 1; i <= n; i++) { costs[i, 0] = i * gap; actions[i, 0] = 1; }
        for (int j = 1; j <= m; j++) { costs[0, j] = j * gap; actions[0, j] = 2; }
        for (int i = 1; i <= n; i++)
        {
            token.ThrowIfCancellationRequested();
            for (int j = 1; j <= m; j++)
            {
                double replace = costs[i - 1, j - 1] + Cost(a[i - 1], b[j - 1]);
                double remove = costs[i - 1, j] + gap, insert = costs[i, j - 1] + gap;
                if (replace <= Math.Min(remove, insert)) { costs[i, j] = replace; actions[i, j] = 0; }
                else if (remove <= insert) { costs[i, j] = remove; actions[i, j] = 1; }
                else { costs[i, j] = insert; actions[i, j] = 2; }
            }
        }
        int x = n, y = m;
        while (x > 0 || y > 0)
        {
            switch (actions[x, y])
            {
                case 0: pairs.Add((left[--x], right[--y])); break;
                case 1: pairs.Add((left[--x], null)); break;
                default: pairs.Add((null, right[--y])); break;
            }
        }
        pairs.Reverse(); return pairs;
    }

    private static (IReadOnlyList<HighlightSpan>, IReadOnlyList<HighlightSpan>) Highlight(string? left, string? right)
    {
        if (left is null) return ([], right is { Length: > 0 } ? [new(0, right.Length)] : []);
        if (right is null) return (left.Length > 0 ? [new(0, left.Length)] : [], []);
        int prefix = 0, suffix = 0;
        while (prefix < Math.Min(left.Length, right.Length) && left[prefix] == right[prefix]) prefix++;
        // Never split a UTF-16 surrogate pair when drawing a highlighted range.
        if (prefix > 0 && prefix < left.Length && char.IsLowSurrogate(left[prefix])) prefix--;
        while (suffix < Math.Min(left.Length, right.Length) - prefix && left[^(suffix + 1)] == right[^(suffix + 1)]) suffix++;
        if (suffix > 0 && suffix < left.Length && char.IsLowSurrogate(left[left.Length - suffix])) suffix--;
        int a = left.Length - prefix - suffix, b = right.Length - prefix - suffix;
        return (a > 0 ? [new(prefix, a)] : [], b > 0 ? [new(prefix, b)] : []);
    }
}
