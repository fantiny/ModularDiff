using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ModularDiff.Contracts;
using ModularDiff.Core;
using ModularDiff.Desktop;

/// <summary>查找替换 —— WPF 版有(查找条上的替换:替换一处、全部替换),macOS 版此前没有。
/// 这里替换的是描边的那一处(所见即所换),全部替换作用于所选的一侧或两侧;每侧是一次编辑,⌘Z 一步还原。
/// 行号必须指得到文件里:行跟着文字走之后才成立,所以另有"屏幕和文件对不上就一个字不写"的一条。</summary>
internal static class ReplaceTests
{
    private static void Pump(Window window, int milliseconds = 150)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Thread.Sleep(10); }
    }

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        HeadlessWindowExtensions.KeyPressQwerty(window, key, modifiers);
        HeadlessWindowExtensions.KeyReleaseQwerty(window, key, modifiers);
        Pump(window, 150);
    }

    private static bool TextRowsShown(MainWindow window) => window.GetVisualDescendants().OfType<AlignedRowsView>().Any(v => v.RowCount > 0);

    private static MainWindow Open(string plugins, string left, string right, Action<bool, string> check, Func<MainWindow, bool>? ready = null, string what = "file compare")
    {
        var window = new MainWindow(["--plugins", plugins, "--left", left, "--right", right, "--auto-compare"]);
        window.Show();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        bool Ready(MainWindow w) => ready is null ? TextRowsShown(w) : ready(w);
        while (!Ready(window) && DateTime.UtcNow < deadline) Pump(window, 20);
        check(Ready(window), $"{what}: the window did not get ready (status: {window.StatusText})");
        Pump(window, 100);
        return window;
    }

    /// <summary>Edits a cell inline and closes the editor with Enter, as the row-follow tests do.</summary>
    private static void EditCell(MainWindow window, int row, bool left, string text, Action<bool, string> check)
    {
        window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout();
        window.BeginEditRow(row, left);
        var editor = window.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "CellEditor" && t.IsVisible);
        check(editor is not null, $"inline cell editor realized (rows {string.Join(" ; ", View(window).Rows.Select(row => $"{Show(row.Left?.Text)}|{Show(row.Right?.Text)}"))}; hex {window.HexViewActive})");
        editor!.Text = text; Dispatcher.UIThread.RunJobs(); Pump(window, 200);
    }

    private static AlignedRowsView View(MainWindow window) => window.GetVisualDescendants().OfType<AlignedRowsView>().Single();
    private static string Show(string? text) => (text ?? "∅").Replace("\n", "|");

    /// <summary>What the search should return, worked out from the rows' texts: every occurrence on both sides, by row, left before right, by start.</summary>
    private static List<(int Row, bool Left, int Start, int Length)> Expected(IReadOnlyList<AlignedRow> rows, string query)
    {
        var found = new List<(int, bool, int, int)>();
        for (int i = 0; i < rows.Count; i++)
            foreach (bool left in new[] { true, false })
            {
                var text = (left ? rows[i].Left : rows[i].Right)?.Text;
                if (string.IsNullOrEmpty(text)) continue;
                for (int at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(query, at + query.Length, StringComparison.OrdinalIgnoreCase))
                    found.Add((i, left, at, query.Length));
            }
        return found;
    }

    /// <summary>Everything the window shows. The language box is left out: each language is named in its own script there.</summary>
    private static string[] ShownText(Window window)
    {
        var controls = window.GetVisualDescendants().OfType<Control>().Where(c => Avalonia.Automation.AutomationProperties.GetAutomationId(c) != "md_tip_lang").ToArray();
        IEnumerable<string?> texts = controls.SelectMany(c => ToolTip.GetTip(c) is { } tip ? new[] { tip.ToString() } : []);
        texts = texts.Concat(controls.OfType<TextBlock>().Select(t => t.Text)).Concat(controls.OfType<TextBox>().Select(t => t.Watermark))
            .Concat(controls.OfType<ComboBox>().SelectMany(box => box.Items.Cast<object?>().Select(item => item?.ToString())));
        return texts.Where(t => !string.IsNullOrEmpty(t)).Select(t => t!).ToArray();
    }

    private static bool FarEast(string? text) => text is not null && text.Any(c => c is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff' or >= '\uac00' and <= '\ud7af');

    public static void Run(string pluginsPath, PluginCatalog plugins, Action<bool, string> check)
    {
        check(Dispatcher.UIThread.CheckAccess() && SynchronizationContext.Current is not null, "replace tests create windows: they need the UI thread and its synchronization context");
        var O = StringComparison.Ordinal; var I = StringComparison.OrdinalIgnoreCase;

        // ───────── 文本算术 ─────────
        check(TextReplace.Occurrences("aaa", "aa", O).SequenceEqual([0]), "occurrences do not overlap");
        check(TextReplace.Occurrences("Two two TWO", "two", I).SequenceEqual([0, 4, 8]) && TextReplace.Occurrences("Two two TWO", "two", O).SequenceEqual([4]), "every occurrence, case as asked");
        check(TextReplace.Occurrences("abc", "", O).Count == 0, "an empty search finds nothing, and does not loop");
        check(TextReplace.ReplaceAll("banana", "an", "x", O, out int count) == "bxxa" && count == 2, "replace all changes every occurrence");
        check(TextReplace.ReplaceAll("banana", "an", "xxxx", O, out count) == "bxxxxxxxxa" && count == 2, "… also when the replacement is longer than the search");
        check(TextReplace.ReplaceAll("banana", "zz", "x", O, out count) == "banana" && count == 0, "nothing to replace: the text as it is, and zero");
        check(TextReplace.ReplaceAll("aaa", "aa", "b", O, out count) == "ba" && count == 1, "a replacement does not feed the next search");
        check(TextReplace.Line("a\r\nbb\nccc", 2) is (3, "bb") && TextReplace.Line("a\r\nbb\nccc", 3) == (6, "ccc"), "where a line begins in the file");
        check(TextReplace.Line("a\r\nbb\nccc", 4) is null && TextReplace.Line("a\r\nbb\nccc", 0) is null, "a line the file does not have");
        var cell = new TextCell(2, "two two", "LF", []);
        check(TextReplace.ReplaceOne("one\ntwo two\n", cell, 4, 3, "two", "2", I) == "one\ntwo 2\n", "the match on screen is the one replaced, wherever the file keeps that line");
        check(TextReplace.ReplaceOne("one\ntwo two\r\nafter", cell with { Ending = "CRLF" }, 0, 3, "two", "2", I) == "one\n2 two\r\nafter", "… in a CRLF file too");
        check(TextReplace.ReplaceOne("one\nTWO two\n", cell, 4, 3, "two", "2", I) is null, "refused when the line on screen is not the line in the file");
        check(TextReplace.ReplaceOne("one\ntwo two\n", cell, 9, 3, "two", "2", I) is null, "refused when the match runs past the line");
        check(TextReplace.ReplaceOne("one\nTWO two\n", cell with { Text = "TWO two" }, 0, 3, "two", "2", O) is null
            && TextReplace.ReplaceOne("one\nTWO two\n", cell with { Text = "TWO two" }, 0, 3, "two", "2", I) == "one\n2 two\n",
            "what is replaced has to be the search text, with the case setting; ignoring case, the upper-case one goes too");
        check(TextReplace.ReplaceOne("one\nTWO two\n", cell, 0, 3, "two", "2", O) is null, "refused when what the screen calls a match is not one with Match case on");
        var matches = new List<(int Row, bool Left, int Start, int Length)> { (1, true, 0, 3), (2, true, 4, 3), (2, true, 9, 3), (4, false, 0, 3) };
        check(TextReplace.AtOrAfter(matches, 2, true, 5) == 2 && TextReplace.AtOrAfter(matches, 2, true, 10) == 3 && TextReplace.AtOrAfter(matches, 2, true, 0) == 1,
            "the first match at or after a place on a line");
        check(TextReplace.AtOrAfter(matches, 4, false, 1) == 0 && TextReplace.AtOrAfter(matches, 9, false, 0) == 0 && TextReplace.AtOrAfter(matches, 0, true, 1) == 0,
            "… wrapping round to the first when nothing follows");
        var bothSides = new List<(int Row, bool Left, int Start, int Length)> { (1, true, 0, 3), (1, false, 0, 3), (2, true, 0, 3) };
        check(TextReplace.AtOrAfter(bothSides, 1, true, 1) == 1 && TextReplace.AtOrAfter(bothSides, 1, false, 1) == 2,
            $"on the same line the right side comes after the left, not the other way round (got {TextReplace.AtOrAfter(bothSides, 1, true, 1)} and {TextReplace.AtOrAfter(bothSides, 1, false, 1)})");
        check(TextReplace.AtOrAfter([], 0, true, 0) == -1 && TextReplace.AtOrAfter(matches, 1, false, 0) == 1, "no matches at all; from the right side of a line the search goes on to the next line");

        string root = Path.Combine(Path.GetTempPath(), "ModularDiff-replace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string left = Path.Combine(root, "left.txt"), right = Path.Combine(root, "right.txt");
        const string leftOriginal = "one\ntwo\nthree\nTWO two\n", rightOriginal = "one\nTWO\nthree\n";
        File.WriteAllText(left, leftOriginal); File.WriteAllText(right, rightOriginal);
        string binA = Path.Combine(root, "a.bin"), binB = Path.Combine(root, "b.bin");
        File.WriteAllBytes(binA, [0x00, 0xFF, 0x00, 0x01]); File.WriteAllBytes(binB, [0x00, 0xFF, 0x00, 0x02]);
        try
        {
            var pair = Open(pluginsPath, left, right, check);
            var view = View(pair);
            var expected = Expected(view.Rows, "two");
            check(expected.Count == 4 && expected[0] == (1, false, 0, 3) && expected.Contains((2, true, 0, 3)) && expected.Contains((4, true, 0, 3)) && expected.Contains((4, true, 4, 3)),
                $"the sample: one match on the right, three on the left with two in one line ({string.Join(" ; ", expected)})");

            // ───────── 不问不露;打开时作用的一侧跟着当前匹配 ─────────
            check(!pair.ReplaceMode && !pair.FindBarVisible, "replace is not shown until it is asked for");
            pair.FindText = "two";
            check(!pair.ReplaceMode, "opening the search does not open replace");
            pair.ShowReplace(); Pump(pair, 100);
            check(pair.ReplaceMode && pair.FindBarVisible && pair.ReplaceBox.IsFocused, "ShowReplace shows the replace row under the search and puts the focus in its box");
            check(pair.ReplaceScope == ReplaceScope.Right, "with the current match on the right, the right side is the one to change");
            pair.FindNext(); pair.ShowReplace();
            check(pair.ReplaceScope == ReplaceScope.Left, "with it on the left, the left side");

            // ───────── 不换的情形 ─────────
            pair.FindText = ""; pair.ReplaceText = "X";
            check(!pair.ReplaceOne() && pair.ReplaceAll() == 0 && pair.StatusText == Lang.Get("status_enter_find") && pair.SessionText == (leftOriginal, rightOriginal),
                $"nothing to find: nothing is replaced, and the status says why (shows '{pair.StatusText}')");
            pair.FindText = "zzz";
            check(!pair.ReplaceOne() && pair.ReplaceAll() == 0 && pair.StatusText == Lang.Get("status_not_found_query", "zzz") && pair.SessionText == (leftOriginal, rightOriginal),
                $"nothing found: nothing is replaced (shows '{pair.StatusText}')");

            // ───────── 替换一处:描边的那一处,在它自己那一侧;然后走到它后面 ─────────
            pair.FindText = "two"; pair.ReplaceText = "2";      // the current match is the first one: the right side's TWO
            check(pair.ReplaceOne() && pair.SessionText == (leftOriginal, "one\n2\nthree\n"), $"the outlined match is replaced, on its side, and nothing else ('{Show(pair.SessionText.Right)}')");
            check(pair.StatusText == Lang.Get("status_replaced_one", "two", "2") && pair.SessionDirty == (false, true), $"the status says so, and only that side is unsaved (shows '{pair.StatusText}')");
            check(view.Rows[1].Right is { Text: "2" } && pair.FindMatch == expected[1], $"the screen shows the replacement, and the search has moved on to the next match (is {pair.FindMatch})");
            pair.ReplaceText = "two two";                       // the replacement contains the search text: the search must not stay on it
            check(pair.ReplaceOne() && pair.SessionText.Left == "one\ntwo two\nthree\nTWO two\n" && pair.FindMatch == (4, true, 0, 3),
                $"a replacement that contains the search text does not hold the search in place (is {pair.FindMatch}, '{Show(pair.SessionText.Left)}')");
            pair.ReplaceText = "T"; pair.ReplaceBox.Focus();
            Press(pair, PhysicalKey.Enter);
            check(pair.SessionText.Left == "one\ntwo two\nthree\nT two\n" && pair.FindMatch == (4, true, 2, 3),
                $"Enter in the replace box replaces the current match, and the search moves to the next match on the line ('{Show(pair.SessionText.Left)}', {pair.FindMatch})");
            check(pair.ReplaceOne() && pair.SessionText.Left == "one\ntwo two\nthree\nT T\n" && pair.FindMatch == (2, true, 0, 3),
                $"after the last match the search comes back to the first (is {pair.FindMatch})");
            Press(pair, PhysicalKey.Escape);
            check(!pair.ReplaceMode && pair.FindBarVisible && pair.FindText == "two", "Esc in the replace box leaves replace and keeps the search");
            pair.Close();

            // ───────── 全部替换:所选一侧的每一处;一步撤销;两侧;区分大小写 ─────────
            var all = Open(pluginsPath, left, right, check);
            var allView = View(all);
            all.FindText = "two"; all.ShowReplace(); Pump(all, 100);
            check(all.ReplaceScope == ReplaceScope.Right, "the scope starts on the side of the current match");
            all.ReplaceScope = ReplaceScope.Left; all.ReplaceText = "x";
            check(all.ReplaceAll() == 3 && all.SessionText == ("one\nx\nthree\nx x\n", rightOriginal),
                $"replace all on the left changes every match there and leaves the right alone ('{Show(all.SessionText.Left)}' / '{Show(all.SessionText.Right)}')");
            check(all.StatusText == Lang.Get("status_replaced_n", 3, "two", "x"), $"the status counts them (shows '{all.StatusText}')");
            check(allView.Rows[2].Left is { Text: "x" } && allView.Rows[4].Left is { Text: "x x" } && allView.Rows[1].Right is { Text: "TWO" }, "and the screen shows it");
            Press(all, PhysicalKey.Z, RawInputModifiers.Meta);
            check(all.SessionText == (leftOriginal, rightOriginal), "⌘Z takes the whole replace-all back in one step");
            Press(all, PhysicalKey.Z, RawInputModifiers.Meta | RawInputModifiers.Shift);
            check(all.SessionText == ("one\nx\nthree\nx x\n", rightOriginal), "⇧⌘Z brings it back");
            Press(all, PhysicalKey.Z, RawInputModifiers.Meta);
            check(all.SessionText == (leftOriginal, rightOriginal), "and ⌘Z once more is the files as they were");
            all.ReplaceScope = ReplaceScope.Both; all.ReplaceText = "y";
            check(all.ReplaceAll() == 4 && all.SessionText == ("one\ny\nthree\ny y\n", "one\ny\nthree\n"),
                $"both sides: three on the left and one on the right ('{Show(all.SessionText.Left)}' / '{Show(all.SessionText.Right)}')");
            check(all.StatusText == Lang.Get("status_replaced_n", 4, "two", "y"), $"the status counts both sides together, 4 and not 3 (shows '{all.StatusText}')");
            Press(all, PhysicalKey.Z, RawInputModifiers.Meta);
            check(all.SessionText == (leftOriginal, rightOriginal), "⌘Z takes both sides back at once");
            all.FindCaseSensitive = true; all.ReplaceScope = ReplaceScope.Left; all.ReplaceText = "z";
            check(all.ReplaceAll() == 2 && all.SessionText == ("one\nz\nthree\nTWO z\n", rightOriginal),
                $"with Match case on only the lower-case ones go ('{Show(all.SessionText.Left)}')");
            Press(all, PhysicalKey.Z, RawInputModifiers.Meta);
            all.FindCaseSensitive = false; all.ReplaceScope = ReplaceScope.Right; all.ReplaceText = "x";
            check(all.ReplaceAll() == 1 && all.SessionText == (leftOriginal, "one\nx\nthree\n") && allView.Rows[1].Right is { Text: "x" },
                $"the right side on its own: the one match there, the left untouched ('{Show(all.SessionText.Right)}')");
            check(all.FindState.Count == 3 && all.FindMatch == (2, true, 0, 3) && all.FindCountText == "1 / 3",
                $"after replace all the search is done again: what is left is found, and the counter says so (state {all.FindState}, '{all.FindCountText}')");
            all.Close();

            // 替换到没有匹配为止:最后一处换掉之后没有当前匹配,计数如实说
            var last = Open(pluginsPath, left, right, check);
            last.FindText = "two"; last.ReplaceText = "q";
            int replaced = 0; for (int guard = 0; guard < 10 && last.ReplaceOne(); guard++) replaced++;
            check(replaced == 4 && last.SessionText == ("one\nq\nthree\nq q\n", "one\nq\nthree\n"), $"every match can be replaced one by one ({replaced} replaced)");
            check(last.FindState == (-1, 0, null) && last.FindCountText == Lang.Get("md_find_none") && !last.ReplaceOne() && last.StatusText == Lang.Get("status_not_found_query", "two"),
                $"with nothing left to find there is nothing to replace (state {last.FindState}, '{last.FindCountText}', status '{last.StatusText}')");
            last.Close();

            // 没有打开文件:只读的提示,什么也不发生
            var empty = new MainWindow(["--plugins", pluginsPath]); empty.Show(); Pump(empty, 100);
            empty.FindText = "two"; empty.ReplaceText = "x";
            check(empty.ReplaceAll() == 0 && empty.ReplaceOne() == false && empty.ReplaceAll() == 0 && empty.SessionText == (null, null) && empty.StatusText == Lang.Get("status_readonly_file"),
                $"with no file open nothing is replaced, and the status says the file is read-only (shows '{empty.StatusText}')");
            empty.Close();

            // ───────── 替换文字里有换行 ─────────
            var split = Open(pluginsPath, left, right, check);
            var splitView = View(split);
            split.FindText = "two"; split.ReplaceText = "a\nb";      // the current match: the right side's TWO
            check(split.ReplaceOne() && split.SessionText == (leftOriginal, "one\na\nb\nthree\n"), $"a replacement with a line break writes two lines ('{Show(split.SessionText.Right)}')");
            check(splitView.RowCount == 6 && splitView.Rows[1].Right is { Text: "a", LineNumber: 2 } && splitView.Rows[2].Right is { Text: "b", LineNumber: 3 }
                && splitView.Rows[3].Left?.Text == "two" && splitView.Rows[4].Right?.LineNumber == 4,
                $"the screen shows the two new lines where the one was ({string.Join(" ; ", splitView.Rows.Select(row => $"{Show(row.Left?.Text)}|{Show(row.Right?.Text)}"))})");
            check(split.FindMatch == (3, true, 0, 3), $"the search goes on to the first match after the replaced place (is {split.FindMatch})");
            check(split.EditorBox(left: false).Text == "one\na\nb\nthree\n", "the edit tab shows it too");
            split.Close();

            // ───────── 屏幕上的行和文件对不上:一个字不写 ─────────
            var stale = Open(pluginsPath, left, right, check);
            var staleView = View(stale);
            staleView.Rows = staleView.Rows.Select(row => row with { Left = row.Left is null ? null : row.Left with { Text = "two " + row.Left.Text } }).ToList();
            stale.FindText = "two";
            for (int guard = 0; stale.FindMatch != (2, true, 0, 3) && guard < 10; guard++) stale.FindNext();
            check(stale.FindMatch == (2, true, 0, 3), "standing on a match the screen shows on a line the file does not have");
            stale.ReplaceText = "x";
            check(!stale.ReplaceOne() && stale.SessionText == (leftOriginal, rightOriginal) && stale.StatusText == Lang.Get("md_replace_mismatch"),
                $"nothing is written where the screen and the file disagree (shows '{stale.StatusText}')");
            check(staleView.Rows[2].Left?.Text == "two", "and the rows are laid out from the file again");
            stale.Close();

            // ───────── 行集合和文件对不上的时候做了别的修改:修改照做,行按文件重排,查找的计数也跟着对 ─────────
            string twinLeft = Path.Combine(root, "twin-L.txt"), twinRight = Path.Combine(root, "twin-R.txt");
            File.WriteAllText(twinLeft, "a\na\nb\n"); File.WriteAllText(twinRight, "a\na\nz\n");
            var relaid = Open(pluginsPath, twinLeft, twinRight, check);
            var relaidView = View(relaid);
            var shownRows = relaidView.Rows.ToArray();
            int hidden = Array.FindIndex(shownRows, row => row.Left?.LineNumber == 2), edited = Array.FindIndex(shownRows, row => row.Left?.Text == "b");
            check(hidden >= 0 && edited >= 0, $"the sample shows the left's lines on its rows ({string.Join(" ; ", shownRows.Select(row => $"{Show(row.Left?.Text)}|{Show(row.Right?.Text)}"))})");
            int editedLine = shownRows[edited].Left!.LineNumber;
            relaidView.Rows = shownRows.Select((row, index) => index == hidden ? row with { Left = null } : row).ToList();   // line 2 is not on the screen any more
            relaid.FindText = "c";
            check(relaid.FindState.Count == 0, "nothing to find before the edit");
            EditCell(relaid, edited, true, "c", check);
            check(relaid.SessionText.Left == "a\na\nc\n", $"the edit is made ('{Show(relaid.SessionText.Left)}')");
            var laidOut = relaidView.Rows.ToArray();
            int changed = Array.FindIndex(laidOut, row => row.Left?.LineNumber == editedLine), back = Array.FindIndex(laidOut, row => row.Left?.LineNumber == 2);
            check(changed >= 0 && laidOut[changed].Left?.Text == "c" && back >= 0 && laidOut[back].Left?.Text == "a"
                && laidOut.Count(row => row.Left is not null) == 3 && laidOut.Count(row => row.Right is not null) == 3,
                $"and the rows are laid out from the file: every line back on a row, once ({string.Join(" ; ", laidOut.Select(row => $"{Show(row.Left?.Text)}|{Show(row.Right?.Text)}"))})");
            check(relaid.FindState.Count == 1 && relaid.FindMatch == (changed, true, 0, 1) && relaid.FindCountText == "1 / 1",
                $"the search and its counter follow the rows that were laid out again (state {relaid.FindState}, '{relaid.FindCountText}')");
            relaid.Close();

            // ───────── 按键;十六进制视图里没有 ─────────
            var keys = Open(pluginsPath, left, right, check);
            Press(keys, PhysicalKey.F, RawInputModifiers.Alt | RawInputModifiers.Meta);
            check(keys.ReplaceMode && keys.FindBarVisible && keys.ReplaceBox.IsFocused, "⌥⌘F opens replace with the focus in its box");
            Press(keys, PhysicalKey.F, RawInputModifiers.Meta);
            check(keys.FindBarVisible && keys.ReplaceMode && keys.FindBox.IsFocused, "⌘F goes back to the search box and leaves the replace row as it is");
            keys.Close();
            var toggle = Open(pluginsPath, left, right, check);
            var toggleButton = toggle.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Concat(toggle.GetVisualDescendants().OfType<Button>())
                .Single(control => Avalonia.Automation.AutomationProperties.GetAutomationId(control) == "md_replace_tip");
            check(!toggle.ReplaceMode && !toggle.FindBarVisible, "the ⇄ button lives in the search bar: with that closed there is no replace");
            toggle.FindText = "two";               // ⌘F: the search bar, with ⇄ at its end
            check(toggle.FindBarVisible && !toggle.ReplaceMode, "the search bar opens without the replace row");
            toggleButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Pump(toggle, 100);
            check(toggle.ReplaceMode && toggle.FindBarVisible, "⇄ opens the replace row");
            toggleButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Pump(toggle, 100);
            check(!toggle.ReplaceMode && toggle.FindBarVisible && toggle.FindText == "two", "⇄ again closes the replace row and leaves the search as it was");
            toggleButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Pump(toggle, 100);
            check(toggle.ReplaceMode, "⇄ opens it again");
            toggle.CloseFind();
            check(!toggle.ReplaceMode && !toggle.FindBarVisible, "closing the search closes replace with it");
            toggle.ReplaceMode = true;
            check(toggle.ReplaceMode && toggle.FindBarVisible, "asking for replace opens the search with it");
            toggle.Close();
            var binary = Open(pluginsPath, binA, binB, check, w => w.HexViewActive, "binary compare");
            Press(binary, PhysicalKey.F, RawInputModifiers.Alt | RawInputModifiers.Meta);
            check(!binary.ReplaceMode && !binary.FindBarVisible, "⌥⌘F does nothing over the hex view");
            binary.Close();

            // ───────── 语言 ─────────
            // 窗口构造时应用的是已保存的语言:设定要像语言下拉框那样,保存 + 应用。
            Palette.SaveLanguage("English"); Lang.Apply("English");
            try
            {
                var english = Open(pluginsPath, left, right, check, what: "file compare in English");
                english.FindText = "two"; english.ShowReplace(); Pump(english, 100);
                string[] shown = ShownText(english);
                check(shown.Contains("Replace") && shown.Contains("Replace all") && shown.Contains("Left side") && shown.Contains("Right side") && shown.Contains("Both sides")
                    && english.ReplaceBox.Watermark == "Replacement text",
                    $"English: the buttons, the sides and the box's hint are in English (shows {string.Join(" | ", shown.Where(t => t.Length < 50).Distinct().Take(16))})");
                string[] foreign = ShownText(english).Where(FarEast).ToArray();
                check(foreign.Length == 0, $"English: no text in a Far-East script: {string.Join(" | ", foreign.Take(6))}");
                english.ReplaceScope = ReplaceScope.Left; english.ReplaceText = "x";
                check(english.ReplaceAll() == 3 && english.StatusText == "Replaced 3 · two → x", $"a message after replace all is English too (shows '{english.StatusText}')");
                english.ReplaceText = "w"; english.FindText = "two";
                check(english.ReplaceOne() && english.StatusText == "Replaced 1 · two → w", $"… after replace one (shows '{english.StatusText}')");
                english.FindText = "zzz"; english.ReplaceOne();
                check(english.StatusText == "Not found: zzz", $"… when nothing is found (shows '{english.StatusText}')");
                // 窗口开着换语言:替换这一行跟着换
                var oneButton = english.GetVisualDescendants().OfType<Button>().Single(button => Avalonia.Automation.AutomationProperties.GetAutomationId(button) == "md_replace_one_tip");
                var allButton = english.GetVisualDescendants().OfType<Button>().Single(button => Avalonia.Automation.AutomationProperties.GetAutomationId(button) == "md_replace_all_tip");
                english.GetVisualDescendants().OfType<ComboBox>().Single(box => Avalonia.Automation.AutomationProperties.GetAutomationId(box) == "md_tip_lang").SelectedItem = "日本語";
                Pump(english, 400);
                check(Equals(oneButton.Content, Lang.Get("replace_one_button")) && Equals(allButton.Content, Lang.Get("replace_all_button"))
                    && english.ReplaceScopeBox.Items.Cast<object?>().SequenceEqual([Lang.Get("md_replace_scope_left"), Lang.Get("md_replace_scope_right"), Lang.Get("md_replace_scope_both")])
                    && english.ReplaceBox.Watermark == Lang.Get("a11y_replace") && (int)english.ReplaceScope == (int)ReplaceScope.Left,
                    $"a language switch reaches the replace row, and keeps the side chosen (buttons '{oneButton.Content}', '{allButton.Content}', scope {english.ReplaceScope})");
                english.Close();
                Palette.SaveLanguage("日本語"); Lang.Apply("日本語");
                var japanese = Open(pluginsPath, left, right, check, what: "file compare in Japanese");
                japanese.FindText = "two"; japanese.ShowReplace(); Pump(japanese, 100);
                string[] ja = ShownText(japanese);
                check(ja.Contains(Lang.Get("replace_one_button")) && ja.Contains(Lang.Get("replace_all_button")) && ja.Contains(Lang.Get("md_replace_scope_both"))
                    && !ja.Any(t => System.Text.RegularExpressions.Regex.IsMatch(t, "\\b(md_replace|status_|replace_)[a-z_]+")),
                    $"Japanese: the replace row reads in Japanese, with no raw key (shows {string.Join(" | ", ja.Where(t => t.Length < 30).Distinct().Take(14))})");
                japanese.ReplaceScope = ReplaceScope.Left; japanese.ReplaceText = "x";
                check(japanese.ReplaceAll() == 3 && japanese.StatusText == Lang.Get("status_replaced_n", 3, "two", "x"), $"the count message comes from the pack (shows '{japanese.StatusText}')");
                japanese.Close();
            }
            finally { Palette.SaveLanguage("中文"); Lang.Apply("中文"); }
        }
        finally
        {
            Palette.SaveLanguage("中文"); Lang.Apply("中文");
            try { Directory.Delete(root, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.WriteLine("replace test files await process exit: " + root); }
        }
    }
}
