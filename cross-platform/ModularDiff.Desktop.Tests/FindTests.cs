using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ModularDiff.Contracts;
using ModularDiff.Core;
using ModularDiff.Desktop;

/// <summary>差异视图里的查找:两侧的行文本都找,匹配处铺底色、当前匹配描边,F3 / ⇧F3 走到下一处 / 上一处。
/// WPF 版一直有查找(Ctrl+F、F3);macOS 版此前没有。</summary>
internal static class FindTests
{
    private static void Pump(Window window, int milliseconds = 150)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Thread.Sleep(10); }
    }

    private static MainWindow Open(string[] args, Action<bool, string> check, Func<MainWindow, bool> ready, string what)
    {
        var window = new MainWindow(args);
        window.Show();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!ready(window) && DateTime.UtcNow < deadline) Pump(window, 20);
        check(ready(window), $"{what}: the window did not get ready (status: {window.StatusText})");
        return window;
    }

    private static bool TextRowsShown(MainWindow window) => window.GetVisualDescendants().OfType<AlignedRowsView>().Any(v => v.RowCount > 0);

    /// <summary>What a find should return, worked out from the rows' texts independently of the view: every occurrence on
    /// both sides, by row, left before right, by start.</summary>
    private static List<(int Row, bool Left, int Start, int Length)> Expected(IReadOnlyList<AlignedRow> rows, string query, bool ignoreCase)
    {
        var found = new List<(int, bool, int, int)>();
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (int i = 0; i < rows.Count; i++)
            foreach (bool left in new[] { true, false })
            {
                var text = (left ? rows[i].Left : rows[i].Right)?.Text;
                if (string.IsNullOrEmpty(text)) continue;
                for (int at = text.IndexOf(query, comparison); at >= 0; at = text.IndexOf(query, at + query.Length, comparison))
                    found.Add((i, left, at, query.Length));
            }
        return found;
    }

    /// <summary>One pixel of the rendered window, as red, green, blue, alpha.</summary>
    private static (byte R, byte G, byte B, byte A) Pixel(Window window, Point point)
    {
        using var frame = window.CaptureRenderedFrame();
        Check(frame is not null && frame.Format == PixelFormat.Rgba8888, $"a rendered frame in RGBA is expected (got {frame?.Format})");
        var buffer = new byte[4];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try { frame!.CopyPixels(new PixelRect((int)Math.Round(point.X), (int)Math.Round(point.Y), 1, 1), pinned.AddrOfPinnedObject(), 4, 4); }
        finally { pinned.Free(); }
        return (buffer[0], buffer[1], buffer[2], buffer[3]);   // RGBA
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    public static void Run(string pluginsPath, PluginCatalog plugins, Action<bool, string> check)
    {
        check(Dispatcher.UIThread.CheckAccess() && SynchronizationContext.Current is not null, "find tests create windows: they need the UI thread and its synchronization context");
        string root = Path.Combine(Path.GetTempPath(), "ModularDiff-find-" + Guid.NewGuid().ToString("N"));
        string pairLeft = Path.Combine(root, "left.txt"), pairRight = Path.Combine(root, "right.txt");
        Directory.CreateDirectory(root);
        File.WriteAllText(pairLeft, "one\ntwo\nthree\ntwo two\n");
        File.WriteAllText(pairRight, "one\nTWO\nthree\n");
        string binA = Path.Combine(root, "a.bin"), binB = Path.Combine(root, "b.bin");
        File.WriteAllBytes(binA, [0x00, 0xFF, 0x00, 0x01]); File.WriteAllBytes(binB, [0x00, 0xFF, 0x00, 0x02]);
        try
        {
            var pair = Open(["--plugins", pluginsPath, "--left", pairLeft, "--right", pairRight, "--auto-compare"], check, TextRowsShown, "file compare");
            var view = pair.GetVisualDescendants().OfType<AlignedRowsView>().Single();
            var rows = view.Rows;
            check(rows.Count >= 4 && rows.Any(row => row.Right is null), $"the left file is longer, so some row has no right side ({rows.Count} rows)");

            // ───────── 默认没有查找条;输入即在两侧找 ─────────
            check(!pair.FindBarVisible, "the find bar is closed until it is asked for");
            var expected = Expected(rows, "two", ignoreCase: true);
            check(expected.Count >= 4 && expected.Any(m => !m.Left) && expected.GroupBy(m => (m.Row, m.Left)).Any(group => group.Count() == 2),
                $"the sample must exercise both sides and two matches in one line (found {expected.Count})");
            pair.FindText = "two";
            check(pair.FindBarVisible && pair.FindState.Count == expected.Count, $"every occurrence on both sides is found: {expected.Count} expected, {pair.FindState.Count} found");
            check(pair.FindState.Index == 0 && pair.FindMatch == expected[0], $"the first match is the first occurrence in the file (is {pair.FindMatch})");
            check(pair.FindCountText == $"1 / {expected.Count}", $"the counter reads 'current / total' (shows '{pair.FindCountText}')");

            // ───────── 下一处按顺序走完并回到第一处;上一处反着走;选中行跟随 ─────────
            for (int step = 1; step <= expected.Count; step++)
            {
                var target = expected[step % expected.Count];
                check(pair.FindNext(), $"step {step}: there is a next match");
                check(pair.FindMatch == target && view.PrimarySelected == target.Row && pair.FindCountText == $"{step % expected.Count + 1} / {expected.Count}",
                    $"step {step}: the next match is {target}, its row selected, counter right (is {pair.FindMatch}, row {view.PrimarySelected}, '{pair.FindCountText}')");
            }
            check(pair.FindNext(backwards: true) && pair.FindMatch == expected[^1] && pair.FindCountText == $"{expected.Count} / {expected.Count}", "backwards from the first match is the last");
            check(pair.FindNext(backwards: true) && pair.FindMatch == expected[^2] && pair.FindCountText == $"{expected.Count - 1} / {expected.Count}", "and the one before that");

            // ───────── 改查询:当前匹配能留则留,否则取原行上最靠前的一处 ─────────
            pair.FindText = "TWO";   // only the right side matches now (case-sensitive spelling is the same ignoring case: 'two' on the left too)
            var upper = Expected(rows, "TWO", ignoreCase: true);
            check(pair.FindState.Count == upper.Count && upper.Count == expected.Count, "ignoring case, 'TWO' finds the same matches as 'two'");
            pair.FindCaseSensitive = true;
            var caseOnly = Expected(rows, "TWO", ignoreCase: false);
            check(pair.FindState.Count == caseOnly.Count && caseOnly.Count == 1 && pair.FindMatch == caseOnly[0],
                $"with Match case on, only the upper-case occurrence is found ({pair.FindState.Count} found, expected {caseOnly.Count})");
            pair.FindCaseSensitive = false;
            check(pair.FindState.Count == expected.Count, "Match case off finds every occurrence again");

            // ───────── 没有匹配 ─────────
            pair.FindText = "nothing-here";
            check(pair.FindState == (-1, 0, null) && pair.FindCountText == Lang.Get("md_find_none") && !pair.FindNext() && !pair.FindNext(true),
                $"no match: nothing to go to, and the counter says so (state {pair.FindState}, '{pair.FindCountText}')");

            // ───────── 像素证据:当前匹配处铺了查找底色,同一位置上没有匹配的行不变 ─────────
            Pump(pair, 200);
            pair.FindText = "two";
            Pump(pair, 200);
            var match = pair.FindMatch!.Value;
            // A row whose same side has no match: it must look the same with and without the search.
            int matchless = Enumerable.Range(0, view.Rows.Count).First(row => ((match.Left ? view.Rows[row].Left : view.Rows[row].Right)?.Text ?? "").Contains("two", StringComparison.OrdinalIgnoreCase) != true);
            Point At(int row, double x) => view.TranslatePoint(new Point(x, row * AlignedRowsView.RowHeight + AlignedRowsView.RowHeight - 6), pair)!.Value;
            double side = match.Left ? 0 : Math.Max(80, view.Bounds.Width / 2);   // the right half begins here
            double matchX = side + 46 + match.Start * view.CharacterWidth + 3;   // past the rounded corner, inside the highlight (the number column is 46 wide)
            var probe = At(match.Row, matchX);
            var control = At(matchless, matchX);
            var with = Pixel(pair, probe);
            var controlWith = Pixel(pair, control);
            pair.FindText = "nothing-here"; Pump(pair, 200);
            var without = Pixel(pair, probe);
            var controlWithout = Pixel(pair, control);
            check(with != without, $"the pixel at the current match is painted when it is a match (with {with}, without {without})");
            // The find colour is amber: on it yellow and red outweigh blue far more than on any row colour.
            bool warm = with.B < without.B - 30 && with.G - with.B > without.G - without.B + 20;
            check(warm, $"the paint is the find colour, a warm tint (with {with}, without {without})");
            check(controlWith == controlWithout, $"a row without a match is not painted at the same place (with {controlWith}, without {controlWithout})");

            // ───────── 按键:⌘F 打开并聚焦,F3 / ⇧F3 走,Enter 在查找框里是下一处,Esc 关闭 ─────────
            pair.CloseFind();
            check(!pair.FindBarVisible && pair.FindState.Count == 0 && pair.FindText == "", "Esc's command closes the bar and clears the search");
            HeadlessWindowExtensions.KeyPressQwerty(pair, PhysicalKey.F, RawInputModifiers.Control); Pump(pair, 100);
            check(pair.FindBarVisible && pair.FindBox.IsFocused && pair.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex == 0,
                $"Cmd+F opens the find bar on the diff tab and puts the focus in its box (visible {pair.FindBarVisible}, focused {pair.FindBox.IsFocused})");
            pair.FindText = "two";
            HeadlessWindowExtensions.KeyPressQwerty(pair, PhysicalKey.F3, RawInputModifiers.None); Pump(pair, 60);
            check(pair.FindState.Index == 1, $"F3 goes to the next match (index {pair.FindState.Index})");
            HeadlessWindowExtensions.KeyPressQwerty(pair, PhysicalKey.F3, RawInputModifiers.Shift); Pump(pair, 60);
            check(pair.FindState.Index == 0, $"Shift+F3 goes back (index {pair.FindState.Index})");
            pair.FindBox.Focus();
            HeadlessWindowExtensions.KeyPressQwerty(pair, PhysicalKey.Enter, RawInputModifiers.None); Pump(pair, 60);
            check(pair.FindState.Index == 1, $"Enter in the find box goes to the next match (index {pair.FindState.Index})");
            HeadlessWindowExtensions.KeyPressQwerty(pair, PhysicalKey.Enter, RawInputModifiers.Shift); Pump(pair, 60);
            check(pair.FindState.Index == 0, $"Shift+Enter goes back (index {pair.FindState.Index})");
            HeadlessWindowExtensions.KeyPressQwerty(pair, PhysicalKey.Escape, RawInputModifiers.None); Pump(pair, 60);
            check(!pair.FindBarVisible && pair.FindState.Count == 0, "Esc in the find box closes it");
            pair.Close();

            // ───────── 十六进制视图里没有查找 ─────────
            var binary = Open(["--plugins", pluginsPath, "--left", binA, "--right", binB, "--auto-compare"], check, w => w.HexViewActive, "binary compare");
            HeadlessWindowExtensions.KeyPressQwerty(binary, PhysicalKey.F, RawInputModifiers.Control); Pump(binary, 100);
            check(!binary.FindBarVisible, "Cmd+F does not open a text search over the hex view");
            binary.Close();

            // ───────── 语言 ─────────
            // 窗口构造时应用的是已保存的语言:设定要像语言下拉框那样,保存 + 应用。
            Palette.SaveLanguage("English"); Lang.Apply("English");
            try
            {
                var english = Open(["--plugins", pluginsPath, "--left", pairLeft, "--right", pairRight, "--auto-compare"], check, TextRowsShown, "file compare in English");
                english.FindText = "nothing-here";
                check(english.FindCountText == Lang.Get("md_find_none") && english.FindCountText == "No matches" && english.FindBox.Watermark == Lang.Get("a11y_find"),
                    $"the counter and the box's hint are English ('{english.FindCountText}', '{english.FindBox.Watermark}')");
                var toggle = english.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(b => Avalonia.Automation.AutomationProperties.GetAutomationId(b) == "md_find_case_tip");
                check(ToolTip.GetTip(toggle)?.ToString() == Lang.Get("md_find_case_tip"), $"the Match case tip is English (shows '{ToolTip.GetTip(toggle)}')");
                english.Close();
            }
            finally { Palette.SaveLanguage("中文"); Lang.Apply("中文"); }
        }
        finally
        {
            Palette.SaveLanguage("中文"); Lang.Apply("中文");
            try { Directory.Delete(root, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.WriteLine("find test files await process exit: " + root); }
        }
    }
}
