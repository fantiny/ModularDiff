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
        TextCell? Cell(DiffLine? line, bool left, IReadOnlyList<HighlightSpan>? spans = null, string? text = null)
        {
            int? number = left ? line?.LeftLine : line?.RightLine;
            return number is null ? null : new(number.Value, text ?? (left ? line!.LeftText : line!.RightText) ?? "",
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
                string? leftText = a?.LeftText, rightText = b?.RightText;
                if (leftText is not null && rightText is not null && leftText.Contains(CellSeparator) && rightText.Contains(CellSeparator))
                    (leftText, rightText) = AlignCells(leftText, rightText);
                var (leftSpans, rightSpans) = Highlight(leftText, rightText);
                rows.Add(new(rows.Count, Cell(a, true, leftSpans, leftText), Cell(b, false, rightSpans, rightText), id));
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

    /// <summary>Table rows (joined with " │ ") align their cells so equal columns stay face to
    /// face: nearby matches keep their slots, insertions/deletions get blank slots.</summary>
    private const string CellSeparator = " │ ";
    private static (string Left, string Right) AlignCells(string left, string right)
    {
        var a = left.Split(CellSeparator);
        var b = right.Split(CellSeparator);
        if ((long)a.Length * b.Length > 4096) return (left, right);
        var outA = new List<string>(); var outB = new List<string>();
        static int Near(string[] values, int from, string value, int limit)
        {
            for (int i = from; i < Math.Min(values.Length, from + limit); i++)
                if (values[i] == value) return i;
            return -1;
        }
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j]) { outA.Add(a[i++]); outB.Add(b[j++]); continue; }
            int bAhead = Near(b, j, a[i], 9), aAhead = Near(a, i, b[j], 9);
            if (bAhead >= 0 && (aAhead < 0 || bAhead - j <= aAhead - i))
                while (j < bAhead) { outA.Add(""); outB.Add(b[j++]); }              // inserted column
            else if (aAhead >= 0)
                while (i < aAhead) { outA.Add(a[i++]); outB.Add(""); }              // deleted column
            else { outA.Add(a[i++]); outB.Add(b[j++]); }                            // changed cell, face to face
        }
        while (i < a.Length) { outA.Add(a[i++]); outB.Add(""); }
        while (j < b.Length) { outA.Add(""); outB.Add(b[j++]); }
        return (string.Join(CellSeparator, outA), string.Join(CellSeparator, outB));
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

    /// <summary>Word-level in-line differences: words unique to either side highlight as separate
    /// small segments (like Beyond Compare) instead of one big prefix/suffix block. Long inputs and
    /// surrogate characters stay on the old prefix/suffix fallback path.</summary>
    private static (IReadOnlyList<HighlightSpan>, IReadOnlyList<HighlightSpan>) Highlight(string? left, string? right)
    {
        if (left is null) return ([], right is { Length: > 0 } ? [new(0, right.Length)] : []);
        if (right is null) return (left.Length > 0 ? [new(0, left.Length)] : [], []);
        if (left == right) return ([], []);
        if (WordSpans(left, out var aWords) && WordSpans(right, out var bWords))
        {
            var leftSpans = new List<HighlightSpan>();
            var rightSpans = new List<HighlightSpan>();
            foreach (var (kind, a, b) in WordDiff(aWords, bWords))
            {
                if (kind == 0) continue;
                if (a is not null) leftSpans.Add(new(a.Value.Start, a.Value.Length));
                if (b is not null) rightSpans.Add(new(b.Value.Start, b.Value.Length));
            }
            return (leftSpans, rightSpans);
        }
        int prefix = 0, suffix = 0;
        while (prefix < Math.Min(left.Length, right.Length) && left[prefix] == right[prefix]) prefix++;
        // Never split a UTF-16 surrogate pair when drawing a highlighted range.
        if (prefix > 0 && prefix < left.Length && char.IsLowSurrogate(left[prefix])) prefix--;
        while (suffix < Math.Min(left.Length, right.Length) - prefix && left[^(suffix + 1)] == right[^(suffix + 1)]) suffix++;
        if (suffix > 0 && suffix < left.Length && char.IsLowSurrogate(left[left.Length - suffix])) suffix--;
        int leftRun = left.Length - prefix - suffix, rightRun = right.Length - prefix - suffix;
        return (leftRun > 0 ? [new(prefix, leftRun)] : [], rightRun > 0 ? [new(prefix, rightRun)] : []);
    }

    /// <summary>Splits text into word spans: identifier runs and single other characters;
    /// whitespace never highlights. Returns false for surrogate characters or >256 words.</summary>
    private static bool WordSpans(string text, out (int Start, int Length, string Word)[] words)
    {
        var spans = new List<(int, int, string)>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (char.IsHighSurrogate(c)) { words = []; return false; }
            int start = i;
            if (char.IsLetterOrDigit(c) || c == '_')
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
            else i++;
            spans.Add((start, i - start, text[start..i]));
            if (spans.Count > 256) { words = []; return false; }
        }
        words = [.. spans];
        return true;
    }

    /// <summary>LCS over word spans: (0, both) equal, (1, left-only), (2, right-only).</summary>
    private static IEnumerable<(int Kind, (int Start, int Length, string Word)? A, (int Start, int Length, string Word)? B)> WordDiff(
        (int Start, int Length, string Word)[] a, (int Start, int Length, string Word)[] b)
    {
        int n = a.Length, m = b.Length;
        var common = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                common[i, j] = a[i].Word == b[j].Word ? common[i + 1, j + 1] + 1 : Math.Max(common[i + 1, j], common[i, j + 1]);
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x].Word == b[y].Word) { yield return (0, a[x], b[y]); x++; y++; }
            else if (common[x + 1, y] >= common[x, y + 1]) { yield return (1, a[x], null); x++; }
            else { yield return (2, null, b[y]); y++; }
        }
        while (x < n) yield return (1, a[x++], null);
        while (y < m) yield return (2, null, b[y++]);
    }
}
