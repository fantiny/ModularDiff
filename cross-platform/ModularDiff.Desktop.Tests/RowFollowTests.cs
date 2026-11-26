using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ModularDiff.Contracts;
using ModularDiff.Core;
using ModularDiff.Desktop;

/// <summary>屏幕上的行跟着文件走。手动重对齐(⚡ 关,也是默认)时编辑不重算左右对齐 —— 这是定下的偏好:打字不自动重排。
/// 但此前编辑之后屏幕上的行就是旧的了:就地改完一关编辑框,那一行又显示旧文字;复制行到对侧是按屏幕上的行重建整侧文本,
/// 于是对侧先前的修改被盖掉,连着复制两行只剩最后一行;差异块复制拿屏幕上的块序号去对重新比较的结果,改的不是选中的那一块。</summary>
internal static class RowFollowTests
{
    private static void Pump(Window window, int milliseconds = 150)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Thread.Sleep(10); }
    }

    private static MainWindow Open(string plugins, string left, string right)
    {
        var window = new MainWindow(["--plugins", plugins, "--left", left, "--right", right, "--auto-compare"]);
        window.Show();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (View(window).RowCount == 0 && DateTime.UtcNow < deadline) Pump(window, 20);
        Pump(window, 100);
        return window;
    }

    private static AlignedRowsView View(MainWindow window) => window.GetVisualDescendants().OfType<AlignedRowsView>().Single();

    private static string Show(string? text) => (text ?? "∅").Replace("\n", "|");
    private static string Layout(AlignedRowsView view) => string.Join(" ; ", view.Rows.Select(row => $"{Show(row.Left?.Text)}|{Show(row.Right?.Text)}"));
    private static string LeftNumbers(AlignedRowsView view) => string.Join(",", view.Rows.Select(row => row.Left?.LineNumber.ToString() ?? "-"));
    private static string RightNumbers(AlignedRowsView view) => string.Join(",", view.Rows.Select(row => row.Right?.LineNumber.ToString() ?? "-"));

    /// <summary>就地编辑一行并按 Enter 收起编辑框(文字实时提交,Enter 只是关掉它)。</summary>
    private static void Edit(MainWindow window, int row, bool left, string text, Action<bool, string> check)
    {
        window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout();
        window.BeginEditRow(row, left);
        var editor = window.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "CellEditor" && t.IsVisible);
        check(editor is not null, "inline cell editor realized");
        editor!.Text = text;   // what a paste does: the text is set in one go, and its change is reported (twice) on a later pass
        Dispatcher.UIThread.RunJobs(); Pump(window, 200);
        HeadlessWindowExtensions.KeyPressQwerty(window, PhysicalKey.Enter, RawInputModifiers.None);
        HeadlessWindowExtensions.KeyReleaseQwerty(window, PhysicalKey.Enter, RawInputModifiers.None);
        Pump(window, 150);
        check(!editor!.IsVisible, "Enter closes the cell editor");
    }

    private static void Click(MainWindow window, string id)
    {
        window.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetAutomationId(b) == id)
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Pump(window, 300);
    }

    private static ToggleButton Auto(MainWindow window) => window.GetVisualDescendants().OfType<ToggleButton>().Single(b => Avalonia.Automation.AutomationProperties.GetAutomationId(b) == "md_tip_auto");

    public static void Run(string pluginsPath, PluginCatalog plugins, Action<bool, string> check)
    {
        check(Dispatcher.UIThread.CheckAccess() && SynchronizationContext.Current is not null, "row-follow tests create windows: they need the UI thread and its synchronization context");
        string root = Path.Combine(Path.GetTempPath(), "ModularDiff-follow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string left = Path.Combine(root, "L.txt"), right = Path.Combine(root, "R.txt");
        const string leftOriginal = "a\nb\nc\nd\ne\nf\ng\n", rightOriginal = "a\nx\nc\nd\ne\nx\ng\n";
        File.WriteAllText(left, leftOriginal); File.WriteAllText(right, rightOriginal);
        const string layout = "a|a ; ∅|x ; b|∅ ; c|c ; d|d ; e|e ; ∅|x ; f|∅ ; g|g";
        try
        {
            // ───────── 就地编辑:编辑框关了以后,那一行显示的是改后的文字;对齐没动 ─────────
            var typed = Open(pluginsPath, left, right); var typedView = View(typed);
            check(Auto(typed).IsChecked != true && typedView.RowCount == 9 && Layout(typedView) == layout, $"manual realign is the default and the sample aligns as nine rows ({Layout(typedView)})");
            Edit(typed, 3, true, "D", check);
            check(typed.SessionText.Left == "a\nb\nD\nd\ne\nf\ng\n", $"the buffer has the edit ('{Show(typed.SessionText.Left)}')");
            check(typedView.RowCount == 9 && Layout(typedView) == "a|a ; ∅|x ; b|∅ ; D|c ; d|d ; e|e ; ∅|x ; f|∅ ; g|g",
                $"the row shows the edited text where it was, and nothing moved ({Layout(typedView)})");
            check(typedView.Rows[3].Left is { LineNumber: 3 }, "the edited row keeps its line number");
            check(typed.StatusText.Contains(Lang.Get("hunks_unaligned")), $"until ⟳ the status says the blocks are not aligned (shows '{typed.StatusText}')");
            // 查找是在屏幕上的行文字里找的:行跟着文字走,匹配也跟着走
            typed.FindCaseSensitive = true; typed.FindText = "E"; Pump(typed, 100);   // the sample has lower-case e's only
            check(typed.FindState.Count == 0 && typed.FindCountText == Lang.Get("md_find_none"), $"before the edit there is no E to find (state {typed.FindState})");
            // ⚡ 开着打字也不重排(打字始终不自动重排),文字照样跟上
            Auto(typed).IsChecked = true;
            Edit(typed, 4, true, "E", check);
            check(typed.FindState.Count == 1 && typed.FindMatch == (4, true, 0, 1) && typed.FindCountText == "1 / 1",
                $"once the row reads E, the search finds it there (state {typed.FindState}, counter '{typed.FindCountText}')");
            typed.FindCaseSensitive = false; typed.CloseFind();
            check(typedView.RowCount == 9 && Layout(typedView) == "a|a ; ∅|x ; b|∅ ; D|c ; E|d ; e|e ; ∅|x ; f|∅ ; g|g",
                $"with ⚡ on, typing still leaves the rows where they are and the row shows what was typed ({Layout(typedView)})");
            Auto(typed).IsChecked = false;
            Click(typed, "md_tip_realign");
            check(!typed.StatusText.Contains(Lang.Get("hunks_unaligned")) && typedView.Rows.Count(row => row.HunkId >= 0) > 0
                && typedView.Rows.Any(row => row.Left?.Text == "D") && typedView.Rows.Any(row => row.Left?.Text == "E"),
                $"⟳ aligns again: blocks are marked, the hint is gone, the edits are still there (layout {Layout(typedView)}, status '{typed.StatusText}')");
            // ⌘Z 一步还原对比较的修改
            HeadlessWindowExtensions.KeyPressQwerty(typed, PhysicalKey.Z, RawInputModifiers.Meta);
            HeadlessWindowExtensions.KeyReleaseQwerty(typed, PhysicalKey.Z, RawInputModifiers.Meta);
            Pump(typed, 400);
            check(typed.SessionText.Left == "a\nb\nD\nd\ne\nf\ng\n" && typed.SessionText.Right == rightOriginal, $"⌘Z undoes the last edit on the comparison ('{Show(typed.SessionText.Left)}')");
            typed.Close();

            // ───────── 编辑框里换了行:多出来的行成为新的一行,后面的行号顺移 ─────────
            var split = Open(pluginsPath, left, right); var splitView = View(split);
            Edit(split, 4, true, "d1\nd2", check);
            check(split.SessionText.Left == "a\nb\nc\nd1\nd2\ne\nf\ng\n", $"the buffer has both lines ('{Show(split.SessionText.Left)}')");
            check(splitView.RowCount == 10 && Layout(splitView) == "a|a ; ∅|x ; b|∅ ; c|c ; d1|d ; d2|∅ ; e|e ; ∅|x ; f|∅ ; g|g",
                $"the second line is a row of its own right below, the rest stays ({Layout(splitView)})");
            check(LeftNumbers(splitView) == "1,-,2,3,4,5,6,-,7,8" && RightNumbers(splitView) == "1,2,-,3,4,-,5,6,-,7",
                $"the line numbers of this side move on from there, the other side's do not (left {LeftNumbers(splitView)}, right {RightNumbers(splitView)})");
            // 书签跟着行走;选中的行也还在
            splitView.ToggleBookmark(9, 3);
            Edit(split, 2, true, "b1\nb2\nb3", check);
            check(splitView.RowCount == 12 && splitView.PrimarySelected == 2 && splitView.Rows[2].Left?.Text == "b1", $"the edited row is the selected one (row {splitView.PrimarySelected} of {splitView.RowCount})");
            check(splitView.GoToBookmark(3) && splitView.PrimarySelected == 11 && splitView.Rows[11].Right?.Text == "g" && splitView.Rows[11].Left?.LineNumber == 10,
                $"a bookmark stays on its line when rows are inserted above it (row {splitView.PrimarySelected}, line {splitView.Rows[splitView.PrimarySelected].Left?.LineNumber})");
            split.Close();

            // ───────── 连着把两行复制到对侧:两行都在 ─────────
            var copies = Open(pluginsPath, left, right); var copiesView = View(copies);
            copies.CopyToSide(copiesView.Rows[2], fromLeft: true); Pump(copies, 300);
            check(copies.SessionText.Right == "a\nx\nb\nc\nd\ne\nx\ng\n", $"the first line copied arrives on the right ('{Show(copies.SessionText.Right)}')");
            check(copiesView.Rows[2].Right is { Text: "b", LineNumber: 3 } && copiesView.RowCount == 9, "the screen shows it on the row it was copied to");
            copies.CopyToSide(copiesView.Rows[7], fromLeft: true); Pump(copies, 300);
            check(copies.SessionText.Right == "a\nx\nb\nc\nd\ne\nx\nf\ng\n",
                $"the second line copied arrives too, and the first one is still there ('{Show(copies.SessionText.Right)}')");
            check(copiesView.Rows[7].Right is { Text: "f", LineNumber: 8 } && copiesView.Rows[2].Right?.Text == "b" && RightNumbers(copiesView) == "1,2,3,4,5,6,7,8,9",
                $"both are on their rows, numbered as they are in the file ({Layout(copiesView)}; right {RightNumbers(copiesView)})");
            copies.Close();

            // ───────── 对侧先前的修改不会被复制盖掉;复制的是这一行现在的文字 ─────────
            var kept = Open(pluginsPath, left, right); var keptView = View(kept);
            Edit(kept, 1, false, "y", check);
            check(kept.SessionText.Right == "a\ny\nc\nd\ne\nx\ng\n" && keptView.Rows[1].Right?.Text == "y", "the fix made on the right is in the buffer and on the screen");
            kept.CopyToSide(keptView.Rows[2], fromLeft: true); Pump(kept, 300);
            check(kept.SessionText.Right == "a\ny\nb\nc\nd\ne\nx\ng\n", $"copying a line to the right keeps the fix made on the right before ('{Show(kept.SessionText.Right)}')");
            kept.Close();

            var fresh = Open(pluginsPath, left, right); var freshView = View(fresh);
            Edit(fresh, 3, true, "D", check);
            fresh.CopyToSide(freshView.Rows[3], fromLeft: true); Pump(fresh, 300);
            check(fresh.SessionText.Right == "a\nx\nD\nd\ne\nx\ng\n", $"what is copied is what the line reads now, not what it read before the edit ('{Show(fresh.SessionText.Right)}')");
            check(freshView.Rows[3].Right is { Text: "D" } && freshView.Rows[3].Left?.Text == "D", "and the screen shows the same text on both sides of that row");
            fresh.Close();

            // ───────── 差异块复制:复制的是选中的那一块,不是"编号相同"的那一块 ─────────
            var block = Open(pluginsPath, left, right); var blockView = View(block);
            Edit(block, 4, true, "D", check);   // a new difference above the block to be copied: the file's blocks are numbered differently now
            check(blockView.Rows[4].Left?.Text == "D" && blockView.Rows[7].HunkId == 1, $"on the screen the block of 'f' is still number 1 (hunk {blockView.Rows[7].HunkId})");
            blockView.SelectRows([blockView.Rows[7]]);
            Click(block, "md_tip_merge_r2l");
            check(block.SessionText.Left == "a\nb\nc\nD\ne\nx\ng\n",
                $"copying the block right → left copies the block that was selected, and leaves the edit alone ('{Show(block.SessionText.Left)}')");
            block.Close();

            var gone = Open(pluginsPath, left, right); var goneView = View(gone);
            Edit(gone, 7, true, "x", check);    // makes that block equal
            goneView.SelectRows([goneView.Rows[7]]);
            Click(gone, "md_tip_merge_r2l");
            check(gone.SessionText.Left == "a\nb\nc\nd\ne\nx\ng\n" && gone.StatusText == Lang.Get("status_not_in_hunk"),
                $"when the edit has made the block equal there is nothing to copy, and the status says so (shows '{gone.StatusText}')");
            gone.Close();

            // ───────── 编辑框里粘贴进多行:之后接着打字,字落在最后一行;CRLF 文件里每一行都还是 CRLF ─────────
            // 程序给编辑框赋文字就是粘贴走的路:改动要到下一轮调度才报上来,而且报两次。
            var pasted = Open(pluginsPath, left, right); var pastedView = View(pasted);
            pasted.BeginEditRow(4, true);
            var cell = pasted.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "CellEditor" && t.IsVisible);
            cell.Text = "d1\nd2"; Dispatcher.UIThread.RunJobs(); Pump(pasted, 250);
            check(pasted.SessionText.Left == "a\nb\nc\nd1\nd2\ne\nf\ng\n", $"pasting two lines into a cell writes two lines, once ('{Show(pasted.SessionText.Left)}')");
            cell.Text += "x"; Dispatcher.UIThread.RunJobs(); Pump(pasted, 250);
            check(pasted.SessionText.Left == "a\nb\nc\nd1\nd2x\ne\nf\ng\n", $"typing after the paste lands on the pasted text's last line ('{Show(pasted.SessionText.Left)}')");
            check(pastedView.Rows[4].Left is { Text: "d1", LineNumber: 4 } && pastedView.Rows[5].Left is { Text: "d2x", LineNumber: 5 }, "and the screen shows both lines as they are");
            pasted.Close();

            string crlfLeft = Path.Combine(root, "crlf-L.txt"), crlfRight = Path.Combine(root, "crlf-R.txt");
            File.WriteAllText(crlfLeft, leftOriginal.Replace("\n", "\r\n")); File.WriteAllText(crlfRight, rightOriginal.Replace("\n", "\r\n"));
            var crlf = Open(pluginsPath, crlfLeft, crlfRight); var crlfView = View(crlf);
            Edit(crlf, 4, true, "d1\nd2", check);
            check(crlf.SessionText.Left == "a\r\nb\r\nc\r\nd1\r\nd2\r\ne\r\nf\r\ng\r\n", $"in a CRLF file both pasted lines end in CRLF ('{Show(crlf.SessionText.Left)}')");
            check(crlfView.Rows[4].Left is { Text: "d1", Ending: "CRLF" } && crlfView.Rows[5].Left is { Text: "d2", Ending: "CRLF" }, "and the screen knows their endings");
            crlf.Close();

            // ───────── 视图自己:选中的行、跨行选区、书签都跟着行走 ─────────
            static AlignedRow Row(string? left, string? right) => new(0, left is null ? null : new TextCell(1, left, "LF", []), right is null ? null : new TextCell(1, right, "LF", []));
            var before = new List<AlignedRow> { Row("a", "x"), Row("b", null), Row("c", "y"), Row(null, "z"), Row("d", "w") };
            var after = new List<AlignedRow> { Row("n", null), Row("a", "x"), Row("B", null), Row("c", "y"), Row("d", "w") };   // a row gained on top, one gone
            var view = new AlignedRowsView { Rows = before };
            view.SelectRows([before[1], before[4]]);
            view.ToggleBookmark(3, 2); view.ToggleBookmark(4, 3);
            view.SetTextSelection(1, 0, 2, 1, true);
            int? selectionBefore = view.TextSelectionRange?.FromRow;
            view.FollowRows(new FollowedRows(after, [1, 2, 3, -1, 4]));
            check(view.PrimarySelected == 2 && view.SelectedRows.SequenceEqual([after[2], after[4]]),
                $"the selection moves with its rows ({view.SelectedRows.Count} rows, primary {view.PrimarySelected}; texts {string.Join(",", view.SelectedRows.Select(row => row.Left?.Text ?? row.Right?.Text))})");
            check(view.TextSelectionRange is { FromRow: 2, ToRow: 3, Left: true }, $"a selection across rows moves too (is {view.TextSelectionRange}, was from row {selectionBefore})");
            check(!view.GoToBookmark(2) && view.PrimarySelected == 2, "a bookmark on a row that disappeared is gone, and the selection is left alone");
            check(view.GoToBookmark(3) && view.PrimarySelected == 4, $"a bookmark on a row that moved goes with it (row {view.PrimarySelected})");
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.WriteLine("row-follow test files await process exit: " + root); }
        }
    }
}
