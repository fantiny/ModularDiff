using ModularDiff.Contracts;
using ModularDiff.Core;
using ModularDiff.Desktop;

/// <summary>RowFollow 的性质测试:随机文字(混着 \n、\r、\r\n)、随机改动、随机拼出来的行集合,几千轮。
/// 手工写的场景只能覆盖想到的情形;这里核对的是不变量 —— 跟随之后,这一侧的格按顺序就是文件的行,另一侧原封不动,
/// 旧行与新行的对应关系成立。行数、行尾这类边角由随机文字自己撞出来。</summary>
internal static class RowFollowProperties
{
    private const string Alphabet = "aab\n\r\nc";

    private static string Show(string? text) => "[" + (text ?? "∅").Replace("\r\n", "⏎").Replace("\r", "↵").Replace("\n", "↓") + "]";

    private static string RandomText(Random random, int most = 12)
    {
        var text = new char[random.Next(most + 1)];
        for (int i = 0; i < text.Length; i++) text[i] = Alphabet[random.Next(Alphabet.Length)];
        return new string(text);
    }

    /// <summary>A random change to the text: a span replaced, one inserted, one deleted, something appended, or nothing.</summary>
    private static string Changed(Random random, string text)
    {
        int at = random.Next(text.Length + 1);
        int length = random.Next(text.Length - at + 1);
        return random.Next(5) switch
        {
            0 => text,
            1 => text[..at] + RandomText(random, 5),
            2 => text[..at] + text[(at + length)..],
            3 => text + RandomText(random, 4),
            _ => text[..at] + RandomText(random, 5) + text[(at + length)..],
        };
    }

    private static TextCell Cell(DocumentLine line, int number) => Cell(line.Text, number, line.Ending switch { "\r\n" => "CRLF", "\n" => "LF", "\r" => "CR", _ => "EOF" });

    /// <summary>Rows that show the lines of <paramref name="mine"/> on one side and those of <paramref name="theirs"/> on the other, paired at random.</summary>
    private static List<AlignedRow> RandomRows(Random random, string mine, string theirs, bool mineOnTheLeft)
    {
        var own = DocumentLines.Parse(mine); var other = DocumentLines.Parse(theirs);
        var rows = new List<AlignedRow>();
        for (int i = 0, j = 0; i < own.Count || j < other.Count;)
        {
            bool takeMine = i < own.Count && (j >= other.Count || random.Next(2) == 0);
            bool pair = takeMine && j < other.Count && random.Next(2) == 0;
            TextCell? left = null, right = null;
            if (takeMine) { if (mineOnTheLeft) left = Cell(own[i], i + 1); else right = Cell(own[i], i + 1); i++; }
            if (pair || !takeMine) { if (mineOnTheLeft) right = Cell(other[j], j + 1); else left = Cell(other[j], j + 1); j++; }
            rows.Add(new AlignedRow(rows.Count, left, right, random.Next(3) == 0 ? 0 : -1));
        }
        return rows;
    }

    private static IEnumerable<TextCell?> Side(IReadOnlyList<AlignedRow> rows, bool left)
    {
        foreach (var row in rows) yield return left ? row.Left : row.Right;
    }

    private static TextCell Cell(string text, int number, string ending = "LF") => new(number, text, ending, []);

    private static string Describe(FollowedRows? followed)
    {
        if (followed is null) return "null";
        return string.Join(" ; ", followed.Rows.Select(row => $"{row.Left?.Text ?? "∅"}|{row.Right?.Text ?? "∅"}")) + " ← " + string.Join(",", followed.OldToNew);
    }

    /// <summary>What Follow promises, case by case, before the random rounds say the same for everything else.</summary>
    private static void Cases(Action<bool, string> check)
    {
        // Rows that are not this text's lines, each on one row, say nothing about where the text is: not followed, compared again instead.
        check(RowFollow.Follow([new(0, Cell("a", 1), null), new(1, Cell("b", 3), null)], true, "a\nb\n", "a\nB\n") is null, "rows that skip a line number are not followed");
        check(RowFollow.Follow([new(0, Cell("a", 1), null), new(1, Cell("b", 1), null)], true, "a\nb\n", "a\nB\n") is null, "rows that show one line number twice are not followed");
        check(RowFollow.Follow([new(0, Cell("a", 1), null)], true, "a\nb\n", "a\nB\n") is null, "rows that do not show one of the text's lines are not followed");
        check(RowFollow.Follow([], true, "a", "b") is null && RowFollow.Follow([new(0, Cell("a", 1), null)], true, "a\n", "a\n") is null, "nothing to follow: no rows, or text that did not change");

        // A new line goes right behind the line it was typed after, as a row of its own — unless the caller says which row it belongs on.
        var gap = new List<AlignedRow> { new(0, Cell("a", 1), null), new(1, null, Cell("x", 1)), new(2, null, Cell("y", 2)), new(3, Cell("b", 2), null) };
        var onItsOwn = RowFollow.Follow(gap, true, "a\nb\n", "a\nn\nb\n");
        check(onItsOwn is not null && Describe(onItsOwn) == "a|∅ ; n|∅ ; ∅|x ; ∅|y ; b|∅ ← 0,2,3,4", $"without a hint the new line takes a row of its own behind the line above ({Describe(onItsOwn)})");
        var onTheRow = RowFollow.Follow(gap, true, "a\nb\n", "a\nn\nb\n", fillRows: [2]);
        check(onTheRow is not null && Describe(onTheRow) == "a|∅ ; ∅|x ; n|y ; b|∅ ← 0,1,2,3", $"with the row named, the new line goes onto it ({Describe(onTheRow)})");
        var both = RowFollow.Follow(gap, true, "a\nb\n", "a\nn1\nn2\nb\n", fillRows: [1, 2]);
        check(both is not null && Describe(both) == "a|∅ ; n1|x ; n2|y ; b|∅ ← 0,1,2,3", $"two new lines fill two named rows, in order ({Describe(both)})");
        var spare = RowFollow.Follow(gap, true, "a\nb\n", "a\nn1\nn2\nb\n", fillRows: [2]);
        check(spare is not null && Describe(spare) == "a|∅ ; ∅|x ; n1|y ; n2|∅ ; b|∅ ← 0,1,2,4", $"a line left over after the named rows takes a row behind the last of them ({Describe(spare)})");
        var outside = RowFollow.Follow(gap, true, "a\nb\n", "a\nn\nb\n", fillRows: [3]);
        check(outside is not null && Describe(outside) == "a|∅ ; n|∅ ; ∅|x ; ∅|y ; b|∅ ← 0,2,3,4", $"a named row that already has a line, or lies outside the gap, is not used ({Describe(outside)})");

        // Line endings: each line keeps what the file has for it; a line that only changed its ending stays on its row.
        var crlf = RowFollow.Follow([new(0, Cell("a", 1, "CRLF"), null), new(1, Cell("b", 2, "CRLF"), null)], true, "a\r\nb\r\n", "a\r\nx\r\nb\r\n");
        check(crlf is not null && crlf.Rows.Select(row => row.Left!).SequenceEqual([Cell("a", 1, "CRLF"), Cell("x", 2, "CRLF"), Cell("b", 3, "CRLF")]), $"CRLF on every line of a CRLF file ({Describe(crlf)})");
        var ending = RowFollow.Follow([new(0, Cell("a", 1, "CR"), null), new(1, Cell("b", 2, "EOF"), null)], true, "a\rb", "a\r\nb");
        check(ending is not null && ending.Rows.Count == 2 && ending.OldToNew.SequenceEqual([0, 1]) && ending.Rows[0].Left! == Cell("a", 1, "CRLF") && ending.Rows[1].Left! == Cell("b", 2, "EOF"),
            $"a line whose ending alone changed keeps its row and number ({Describe(ending)})");
        var appended = RowFollow.Follow([new(0, Cell("a", 1), null), new(1, Cell("b", 2, "EOF"), null)], true, "a\nb", "a\nb\nc");
        check(appended is not null && appended.Rows.Select(row => row.Left!).SequenceEqual([Cell("a", 1), Cell("b", 2), Cell("c", 3, "EOF")]) && appended.OldToNew.SequenceEqual([0, 1]),
            $"a line appended after one without an ending: the old last line gains the ending, the new one has none ({Describe(appended)})");

        // A line that goes from between two others: those two keep their rows, they are not paired up in order with what is left.
        var thinned = RowFollow.Follow([new(0, Cell("a", 1), Cell("p", 1)), new(1, Cell("x", 2), Cell("q", 2)), new(2, Cell("b", 3), Cell("r", 3))], true, "a\nx\nb\n", "a\nb\n");
        check(thinned is not null && Describe(thinned) == "a|p ; ∅|q ; b|r ← 0,1,2", $"when the line between two others goes, they keep their rows ({Describe(thinned)})");
        // Two changes and a deleted line in one edit (a file-type tool can do that): b is in both texts and keeps its row; the lines
        // around it are not simply paired up in order, which would put b opposite the wrong one.
        var twice = RowFollow.Follow([new(0, Cell("a", 1), Cell("p", 1)), new(1, Cell("x", 2), Cell("q", 2)), new(2, Cell("b", 3), Cell("r", 3)), new(3, Cell("c", 4), Cell("s", 4))],
            true, "a\nx\nb\nc", "A\nb\nC");
        check(twice is not null && Describe(twice) == "A|p ; ∅|q ; b|r ; C|s ← 0,1,2,3", $"a line that is in both texts keeps its row in a wider change ({Describe(twice)})");
        // A line number the rows skip with the count right all the same: nothing says where in the text that line is.
        check(RowFollow.Follow([new(0, Cell("a", 1), null), new(1, Cell("c", 3), null)], true, "a\nb\nc\n", "a\nB\nc\n") is null, "rows that skip a line in the middle are not followed");

        // Two separate changes in one edit: lines that are in both texts keep their rows, wherever they are in the changed stretch.
        var scattered = RowFollow.Follow([new(0, Cell("a", 1), null), new(1, Cell("b", 2), null), new(2, Cell("c", 3), null), new(3, Cell("d", 4), null)], true, "a\nb\nc\nd", "A\nb\nc\nD");
        check(scattered is not null && Describe(scattered) == "A|∅ ; b|∅ ; c|∅ ; D|∅ ← 0,1,2,3", $"the lines that stayed keep their rows in the middle of a wider change ({Describe(scattered)})");
    }

    public static void Run(Action<bool, string> check)
    {
        Cases(check);
        var random = new Random(20261126);

        // ───────── 数行:与契约里的拆行逐个位置对照 ─────────
        int counted = 0;
        for (int round = 0; round < 1500; round++)
        {
            string text = RandomText(random);
            var lines = DocumentLines.Parse(text);
            var wrong = new List<string>();
            if (RowFollow.TotalLines(text) != lines.Count) wrong.Add($"total {RowFollow.TotalLines(text)} ≠ {lines.Count}");
            for (int upto = 0; upto <= text.Length; upto++)
            {
                int expected = lines.Count(line => line.Offset + line.Length <= upto);
                if (RowFollow.EndedLines(text, upto) != expected) wrong.Add($"ended before {upto}: {RowFollow.EndedLines(text, upto)} ≠ {expected}");
            }
            for (int from = 0; from <= text.Length; from++)
            {
                int expected = lines.Count(line => line.Offset >= from);
                if (RowFollow.StartedLines(text, from) != expected) wrong.Add($"started from {from}: {RowFollow.StartedLines(text, from)} ≠ {expected}");
            }
            if (wrong.Count > 0) check(false, $"counting lines of {Show(text)}: {string.Join("; ", wrong.Take(4))}");
            counted++;
        }
        check(counted == 1500, "counting lines: 1500 random texts agree with DocumentLines.Parse at every position");

        // ───────── 跟随:不变量 ─────────
        int followed = 0, addedRows = 0, removedRows = 0;
        for (int round = 0; round < 2500; round++)
        {
            string oldText = RandomText(random), theirs = RandomText(random), newText = Changed(random, oldText);
            bool left = random.Next(2) == 0;   // the side followed shows the old text; the other shows an unrelated text
            var rows = RandomRows(random, oldText, theirs, mineOnTheLeft: left);
            if (rows.Count == 0) continue;   // both texts empty: nothing on the screen to follow
            var wrong = new List<string>();
            var result = RowFollow.Follow(rows, left, oldText, newText);
            string what = $"round {round}: {Show(oldText)} → {Show(newText)}, other {Show(theirs)}, following the {(left ? "left" : "right")} side";
            if (oldText == newText)
            {
                if (result is not null) wrong.Add("an unchanged text was followed");
            }
            else if (result is null) wrong.Add("not followed: the rows were laid out from this very text, and the change is tiny");
            else
            {
                followed++;
                var lines = DocumentLines.Parse(newText);
                var cells = Side(result.Rows, left).Where(cell => cell is not null).Select(cell => cell!).ToArray();
                if (cells.Length != lines.Count) wrong.Add($"{cells.Length} cells for {lines.Count} lines");
                else for (int i = 0; i < lines.Count; i++)
                    if (cells[i].Text != lines[i].Text || cells[i].Ending != Cell(lines[i], i + 1).Ending || cells[i].LineNumber != i + 1)
                    { wrong.Add($"line {i + 1} reads {Show(cells[i].Text)}/{cells[i].Ending}/{cells[i].LineNumber}, the file has {Show(lines[i].Text)}/{Cell(lines[i], i + 1).Ending}"); break; }
                var oldOther = Side(rows, !left).Where(cell => cell is not null).Select(cell => cell!).ToArray();
                var newOther = Side(result.Rows, !left).Where(cell => cell is not null).Select(cell => cell!).ToArray();
                if (!newOther.SequenceEqual(oldOther)) wrong.Add("the other side's cells changed");   // same objects, same order
                if (result.Rows.Any(row => row.Left is null && row.Right is null)) wrong.Add("a row with nothing on it");
                if (result.OldToNew.Count != rows.Count) wrong.Add("the map has an entry per old row");
                var targets = new HashSet<int>();
                for (int i = 0; i < rows.Count && wrong.Count < 4; i++)
                {
                    int to = result.OldToNew[i];
                    if (to < 0)
                    {
                        if ((left ? rows[i].Right : rows[i].Left) is not null) wrong.Add($"old row {i} had the other side and was dropped");
                        removedRows++;
                    }
                    else if (!targets.Add(to)) wrong.Add($"old row {i} shares its new row with another");
                    else if (!ReferenceEquals((left ? result.Rows[to].Right : result.Rows[to].Left), (left ? rows[i].Right : rows[i].Left))) wrong.Add($"old row {i} lost its other side");
                }
                // A row that no old row became is a new one: it carries a line of this side and nothing of the other.
                addedRows += result.Rows.Count - targets.Count;
                for (int i = 0; i < result.Rows.Count && wrong.Count < 4; i++)
                    if (!targets.Contains(i) && ((left ? result.Rows[i].Right : result.Rows[i].Left) is not null || (left ? result.Rows[i].Left : result.Rows[i].Right) is null))
                        wrong.Add($"new row {i} is not a line of this side on its own");
            }
            check(wrong.Count == 0, $"{what}: {string.Join("; ", wrong.Take(4))}");
        }
        check(followed > 1200 && addedRows > 500 && removedRows > 300,
            $"following: {followed} changes followed, {addedRows} rows added, {removedRows} rows removed (the cases have to reach all of these)");
    }
}
