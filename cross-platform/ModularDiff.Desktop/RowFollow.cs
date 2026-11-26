using ModularDiff.Contracts;

namespace ModularDiff.Desktop;

/// <summary>一次"行跟着文字走"的结果:新的一组行,和每个旧行去了哪里(-1:两侧都空了,这一行去掉)。</summary>
internal sealed record FollowedRows(IReadOnlyList<AlignedRow> Rows, IReadOnlyList<int> OldToNew);

/// <summary>一侧的文字改了,而左右的对齐先不重算(手动重对齐,或者打字的时候):把屏幕上这一侧的行改成文件里现在的样子。
/// 对齐的骨架不动 —— 没变的行留在原来的行上;成块替换的行按顺序接管原来的行;多出来的行紧跟在后面成为新行;没了的去掉那一格;
/// 后面的行号顺移。行集合和这份文字对不上时返回 null:调用方改为重新比较,不去猜。</summary>
internal static class RowFollow
{
    private static readonly char[] Breaks = ['\r', '\n'];

    /// <summary>An edit too scattered to follow line by line (a formatter over a whole file, say): comparing again is cheaper and truer.</summary>
    private const long MostComparedLines = 1_000_000;

    /// <summary><paramref name="fillRows"/>:这一侧还没有格子的行,调用方知道新多出来的行就该落在那里(把一行复制到对侧,就是被复制的那一行)。
    /// 按顺序一一落进去;多出来的,以及没有提示的,另起一行紧跟在后面。</summary>
    public static FollowedRows? Follow(IReadOnlyList<AlignedRow> rows, bool left, string oldText, string newText, IReadOnlyList<int>? fillRows = null)
    {
        if (rows.Count == 0 || oldText == newText) return null;
        // What changed: the text before it and after it is the same in both, as the session finds for the edit itself.
        int head = 0;
        while (head < oldText.Length && head < newText.Length && oldText[head] == newText[head]) head++;
        int tail = 0;
        while (tail < oldText.Length - head && tail < newText.Length - head && oldText[^(tail + 1)] == newText[^(tail + 1)]) tail++;
        int oldEnd = oldText.Length - tail, newEnd = newText.Length - tail;

        // Which row shows each line of the old text on this side. The lines the screen shows have to be this text's lines, each on one row:
        // anything else means the rows were laid out from something else, and then an offset into them says nothing.
        int count = 0;
        foreach (var row in rows)
            if ((left ? row.Left : row.Right) is { } shown) count = Math.Max(count, shown.LineNumber);
        if (TotalLines(oldText) != count) return null;
        var rowOf = new int[count];
        Array.Fill(rowOf, -1);
        for (int index = 0; index < rows.Count; index++)
        {
            if ((left ? rows[index].Left : rows[index].Right) is not { } shown) continue;
            if (shown.LineNumber < 1 || shown.LineNumber > count || rowOf[shown.LineNumber - 1] >= 0) return null;
            rowOf[shown.LineNumber - 1] = index;
        }
        foreach (int row in rowOf) if (row < 0) return null;

        // The lines wholly before the change and the ones from its end on are the same lines in both texts. What lies between is replaced.
        int first = Math.Min(EndedLines(oldText, head), EndedLines(newText, head));
        int kept = Math.Min(StartedLines(oldText, oldEnd), StartedLines(newText, newEnd));
        int oldMiddle = count - first - kept, newMiddle = TotalLines(newText) - first - kept;
        if (oldMiddle < 0 || newMiddle < 0 || (oldMiddle + 1L) * (newMiddle + 1) > MostComparedLines) return null;
        var oldLines = new string[oldMiddle];
        for (int k = 0; k < oldMiddle; k++) oldLines[k] = (left ? rows[rowOf[first + k]].Left : rows[rowOf[first + k]].Right)!.Text;
        // The new middle lines, read from the text. The middle begins after the `first`-th line ending, which ends before the change.
        var newLines = new List<(string Text, string Ending)>(newMiddle);
        int at = 0;
        for (int line = 0; line < first; line++) { if (!NextLine(newText, ref at) || at > head) return null; }
        for (int line = 0; line < newMiddle; line++)
        {
            if (!ReadLine(newText, ref at, out string text, out string ending)) return null;
            newLines.Add((text, ending));
        }

        // Lines that are in both middles keep their rows. Between two of those, the old lines and the new ones stand in for each other in order;
        // what is left over goes, or is added right behind the last one that stayed.
        var same = Match(oldLines, newLines.Select(line => line.Text).ToArray());
        var taken = new int[oldMiddle];      // for an old middle line: the new line that takes over its row, -1 when its cell goes
        Array.Fill(taken, -1);
        var filled = new Dictionary<int, int>();   // a row without a cell on this side that one of the new lines goes into
        var added = new List<(bool Before, int Row, int NewLine, int Hunk)>();   // new lines that get a row of their own
        for (int i = 0, j = 0; i < oldMiddle || j < newMiddle;)
        {
            if (i < oldMiddle && j < newMiddle && same[i] == j) { taken[i++] = j++; continue; }
            int oldStop = i, newStop = j;
            while (oldStop < oldMiddle && same[oldStop] < 0) oldStop++;
            newStop = oldStop < oldMiddle ? same[oldStop] : newMiddle;
            int paired = Math.Min(oldStop - i, newStop - j);
            for (int k = 0; k < paired; k++) taken[i + k] = j + k;
            // Where the gap lies: behind the last line of this side that stays (the last one paired, else the one before the gap, else the
            // last line before the change), and in front of the first one after it.
            int behind = paired > 0 ? rowOf[first + i + paired - 1] : i > 0 ? rowOf[first + i - 1] : first > 0 ? rowOf[first - 1] : -1;
            int ahead = oldStop < oldMiddle ? rowOf[first + oldStop] : first + oldStop < count ? rowOf[first + oldStop] : rows.Count;
            var slots = new Queue<int>((fillRows ?? []).Where(row => row > behind && row < ahead && (left ? rows[row].Left : rows[row].Right) is null).Order());
            int last = behind;
            for (int line = j + paired; line < newStop; line++)
            {
                if (slots.Count > 0) { last = slots.Dequeue(); filled[last] = line; continue; }
                // With nothing of this side above it (the top of the text) a line goes in front of the one after; when the side had no
                // lines at all, at the very top.
                bool before = last < 0;
                int anchor = before ? (ahead < rows.Count ? ahead : -1) : last;
                added.Add((before, anchor, line, anchor >= 0 ? rows[anchor].HunkId : -1));
            }
            i = oldStop; j = newStop;
        }

        int shift = newMiddle - oldMiddle;
        TextCell Cell(int line) => new(first + line + 1, newLines[line].Text, newLines[line].Ending, []);
        AlignedRow New(int line, int hunk) => new(-1, left ? Cell(line) : null, left ? null : Cell(line), hunk);
        var beforeRow = added.Where(item => item.Before).ToLookup(item => item.Row, item => (item.NewLine, item.Hunk));
        var afterRow = added.Where(item => !item.Before).GroupBy(item => item.Row).ToDictionary(group => group.Key, group => group.ToArray());

        var result = new List<AlignedRow>(rows.Count + added.Count);
        foreach (var (line, hunk) in beforeRow[-1]) result.Add(New(line, hunk));   // at the very top, in front of every old row
        var map = new int[rows.Count];
        for (int index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            foreach (var (line, hunk) in beforeRow[index]) result.Add(New(line, hunk));
            AlignedRow? updated = row;
            if ((left ? row.Left : row.Right) is { } cell)
            {
                int line = cell.LineNumber - 1;
                TextCell? next;
                if (line < first) next = cell;   // before the change: the same line, the same number
                else if (line >= count - kept) next = cell with { LineNumber = line + shift + 1 };
                else if (taken[line - first] < 0) next = null;
                else
                {
                    int to = taken[line - first];
                    next = cell with { LineNumber = first + to + 1, Ending = newLines[to].Ending, Text = newLines[to].Text, Highlights = [] };
                }
                if (next is null && (left ? row.Right : row.Left) is null) { map[index] = -1; continue; }   // nothing on either side any more
                updated = left ? row with { Left = next } : row with { Right = next };
            }
            else if (filled.TryGetValue(index, out int into)) updated = left ? updated with { Left = Cell(into) } : updated with { Right = Cell(into) };
            map[index] = result.Count;
            result.Add(updated);
            if (afterRow.TryGetValue(index, out var behind)) foreach (var item in behind) result.Add(New(item.NewLine, item.Hunk));
        }
        return new FollowedRows(result, map);
    }

    /// <summary>For each line of <paramref name="a"/>: the line of <paramref name="b"/> it is paired with in their longest common subsequence, else -1.</summary>
    private static int[] Match(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length;
        var length = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                length[i, j] = a[i] == b[j] ? length[i + 1, j + 1] + 1 : Math.Max(length[i + 1, j], length[i, j + 1]);
        var same = new int[n];
        Array.Fill(same, -1);
        for (int i = 0, j = 0; i < n && j < m;)
            if (a[i] == b[j]) { same[i] = j; i++; j++; }
            else if (length[i + 1, j] >= length[i, j + 1]) i++;
            else j++;
        return same;
    }

    /// <summary>The lines of <paramref name="text"/>.</summary>
    internal static int TotalLines(string text) => EndedLines(text, text.Length);

    /// <summary>The lines that end at or before <paramref name="upto"/>: every line ending (\r\n, \r, \n) that ends within [0, upto],
    /// and the last line when it has no ending of its own and lies within [0, upto] as well.</summary>
    internal static int EndedLines(string text, int upto)
    {
        var span = text.AsSpan(0, upto);
        int ended = span.Count('\n') + span.Count('\r') - span.Count("\r\n");
        // A \r in the last place followed by \n is the first half of a \r\n that ends after `upto`: not an ending of its own.
        if (upto > 0 && upto < text.Length && text[upto - 1] == '\r' && text[upto] == '\n') ended--;
        // The last line has no ending when the text does not end with one; it ends where the text does.
        else if (upto >= text.Length && text.Length > 0 && !Breaks.Contains(text[^1])) ended++;
        return ended;
    }

    /// <summary>Lines that start at or after <paramref name="from"/>: the one starting right there, and one after every line ending from there on.</summary>
    internal static int StartedLines(string text, int from)
    {
        var span = text.AsSpan(from);
        int started = span.Count('\n') + span.Count('\r') - span.Count("\r\n");
        if (from > 0 && from < text.Length && (text[from - 1] == '\n' || (text[from - 1] == '\r' && text[from] != '\n'))) started++;   // the line starting at `from` itself
        if (from < text.Length && Breaks.Contains(text[^1])) started--;   // an ending at the very end has no line after it
        return started + (from == 0 && text.Length > 0 ? 1 : 0);
    }

    private static bool NextLine(string text, ref int at)
    {
        if (at >= text.Length) return false;
        int stop = text.IndexOfAny(Breaks, at);
        if (stop < 0) return false;
        at = stop + (text[stop] == '\r' && stop + 1 < text.Length && text[stop + 1] == '\n' ? 2 : 1);
        return true;
    }

    private static bool ReadLine(string text, ref int at, out string line, out string ending)
    {
        line = ending = "";
        if (at >= text.Length) return false;
        int stop = text.IndexOfAny(Breaks, at);
        if (stop < 0) { line = text[at..]; ending = "EOF"; at = text.Length; return true; }
        line = text[at..stop];
        if (text[stop] == '\r' && stop + 1 < text.Length && text[stop + 1] == '\n') { ending = "CRLF"; at = stop + 2; }
        else { ending = text[stop] == '\r' ? "CR" : "LF"; at = stop + 1; }
        return true;
    }
}
