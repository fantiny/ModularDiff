using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ModularDiff.Contracts;
using ModularDiff.Core;
using ModularDiff.Desktop;

/// <summary>快捷键与拖放 —— WPF 版有、macOS 版此前没有的那部分:⌘S 保存聚焦的一侧(两侧都没有焦点时保存两侧),
/// ⇧⌘S 保存两侧,F5 重新读取磁盘,⌘Z / ⇧⌘Z 在差异页撤销 / 重做对比较本身的修改,十六进制视图里 ⌘S 把改过的字节写盘;
/// 把文件拖进窗口时填入路径。</summary>
internal static class ShortcutTests
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
        Pump(window, 200);
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

    private static string RightText(MainWindow window) => window.EditorBox(left: false).Text ?? "";
    private static bool TextRowsShown(MainWindow window) => window.GetVisualDescendants().OfType<AlignedRowsView>().Any(v => v.RowCount > 0);

    private static void EditRow(MainWindow window, int row, bool left, string text, Action<bool, string> check)
    {
        window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout();
        window.BeginEditRow(row, left);
        var cellEditor = window.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "CellEditor" && t.IsVisible);
        check(cellEditor is not null, "inline cell editor realized");
        cellEditor!.Text = text;
        Dispatcher.UIThread.RunJobs(); Pump(window, 400);
    }

    /// <summary>A file as a drag carries it: the platform's own file item, whose path is a file URI — where TryGetLocalPath reads it.</summary>
    private static IStorageItem Dropped(string path) => (IStorageItem)Activator.CreateInstance(
        typeof(IStorageItem).Assembly.GetType("Avalonia.Platform.Storage.FileIO.BclStorageFile", throwOnError: true)!,
        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, null, [new FileInfo(path)], null)!;

    private static DataTransfer Files(params string[] paths)
    {
        var transfer = new DataTransfer();
        foreach (string path in paths) transfer.Add(DataTransferItem.CreateFile(Dropped(path)));
        return transfer;
    }

    /// <summary>A drop as the platform delivers it: the drag enters, moves over the point, then drops there.</summary>
    private static void Drop(Window window, Point point, DataTransfer transfer)
    {
        foreach (var type in new[] { Avalonia.Input.Raw.RawDragEventType.DragEnter, Avalonia.Input.Raw.RawDragEventType.DragOver, Avalonia.Input.Raw.RawDragEventType.Drop })
            HeadlessWindowExtensions.DragDrop(window, point, type, transfer, DragDropEffects.Copy, RawInputModifiers.None);
        Pump(window, 150);
    }

    public static void Run(string pluginsPath, PluginCatalog plugins, Action<bool, string> check)
    {
        check(Dispatcher.UIThread.CheckAccess() && SynchronizationContext.Current is not null, "shortcut tests create windows: they need the UI thread and its synchronization context");
        string root = Path.Combine(Path.GetTempPath(), "ModularDiff-shortcuts-" + Guid.NewGuid().ToString("N"));
        string Read(string name) { Directory.CreateDirectory(root); return Path.Combine(root, name); }
        void Put(string name, string text) => File.WriteAllText(Read(name), text);
        string Disk(string name) => File.ReadAllText(Read(name)).Replace("\n", "|");
        static string Hex(string name) => Convert.ToHexString(File.ReadAllBytes(name));
        Put("L.txt", "one\ntwo\n"); Put("R.txt", "one\nTWO\n");
        try
        {
            // ───────── ⌘S on the diff tab saves both sides ─────────
            var both = Open(["--plugins", pluginsPath, "--left", Read("L.txt"), "--right", Read("R.txt"), "--auto-compare"], check, TextRowsShown, "file compare");
            EditRow(both, 0, true, "ONE", check);   // left line 1
            EditRow(both, 1, false, "two", check);  // right line 2
            check(both.StatusText.Contains(Lang.Get("edit_status_dirty")) && File.ReadAllText(Read("L.txt")) == "one\ntwo\n", "both edits are unsaved: the status says so and the file on disk is as it was");
            Press(both, PhysicalKey.S, RawInputModifiers.Control);
            check(File.ReadAllText(Read("L.txt")) == "ONE\ntwo\n" && File.ReadAllText(Read("R.txt")) == "one\ntwo\n",
                $"Cmd+S on the diff tab writes both sides ({Disk("L.txt")}, {Disk("R.txt")})");
            check(both.StatusText.Contains(Lang.Get("edit_status_saved")), $"and the status is saved again (shows '{both.StatusText}')");
            both.Close();

            // ───────── ⌘S on the edit tab saves the side that has the focus; ⇧⌘S saves both ─────────
            Put("L.txt", "one\ntwo\n"); Put("R.txt", "one\nTWO\n");
            var side = Open(["--plugins", pluginsPath, "--left", Read("L.txt"), "--right", Read("R.txt"), "--auto-compare"], check, TextRowsShown, "file compare");
            var tabs = side.GetVisualDescendants().OfType<TabControl>().Single();
            tabs.SelectedIndex = 1; Pump(side);
            side.EditorBox(left: true).Text = "ONE\ntwo\n"; Pump(side, 300);   // one box at a time, as a person types
            side.EditorBox(left: false).Text = "one\ntwo\n"; Pump(side, 400);
            side.EditorBox(left: true).Focus(); Pump(side, 100);
            check(side.SessionDirty == (true, true), $"both sides have an unsaved change ({side.SessionDirty})");
            Press(side, PhysicalKey.S, RawInputModifiers.Control);
            check(File.ReadAllText(Read("L.txt")) == "ONE\ntwo\n" && File.ReadAllText(Read("R.txt")) == "one\nTWO\n",
                $"Cmd+S saves the side with the focus and leaves the other one alone ({Disk("L.txt")}, {Disk("R.txt")})");
            check(side.SessionDirty == (false, true), $"the other side is still unsaved ({side.SessionDirty}; status '{side.StatusText}')");
            Press(side, PhysicalKey.S, RawInputModifiers.Control | RawInputModifiers.Shift);
            check(File.ReadAllText(Read("R.txt")) == "one\ntwo\n" && side.StatusText.Contains(Lang.Get("edit_status_saved")),
                $"Shift+Cmd+S saves both sides ({Disk("R.txt")}; status {side.StatusText})");

            // ───────── F5 reads the files again ─────────
            File.WriteAllText(Read("R.txt"), "one\ntwo\nthree\n");   // a line added on disk, behind the window's back
            Press(side, PhysicalKey.F5);
            tabs.SelectedIndex = 0; Pump(side, 100);
            var refreshed = side.GetVisualDescendants().OfType<AlignedRowsView>().Single();
            check(refreshed.Rows.Any(row => row.Right?.Text == "three") && side.StatusText.Contains(Lang.Get("edit_status_saved")),
                $"F5 compares again from disk: the line added on disk is in the view ({refreshed.RowCount} rows; status '{side.StatusText}')");
            side.Close();

            // ───────── ⌘Z / ⇧⌘Z on the diff tab: a change made to the comparison is taken back and put again ─────────
            Put("L.txt", "one\ntwo\n"); Put("R.txt", "one\nTWO\n");
            var undo = Open(["--plugins", pluginsPath, "--left", Read("L.txt"), "--right", Read("R.txt"), "--auto-compare"], check, TextRowsShown, "file compare");
            var undoTabs = undo.GetVisualDescendants().OfType<TabControl>().Single();
            undoTabs.SelectedIndex = 1; Pump(undo);
            undo.EditorBox(left: false).Text = "one\ntwo\n";      // typed on the edit tab: the right side now differs from its file
            Pump(undo, 400);
            undoTabs.SelectedIndex = 0; Pump(undo);
            undo.GetVisualDescendants().OfType<AlignedRowsView>().Single().Focus(); Pump(undo, 100);
            check(undo.StatusText.Contains(Lang.Get("edit_status_dirty")), $"a change made to the comparison is unsaved (shows '{undo.StatusText}')");
            Press(undo, PhysicalKey.Z, RawInputModifiers.Control);
            check(undo.StatusText.Contains(Lang.Get("edit_status_saved")) && undo.EditorBox(left: false).Text == "one\nTWO\n",
                $"Cmd+Z on the diff tab takes the change back (shows '{undo.StatusText}'; the right side reads '{RightText(undo).Replace("\n", "|")}')");
            Press(undo, PhysicalKey.Z, RawInputModifiers.Control | RawInputModifiers.Shift);
            check(undo.StatusText.Contains(Lang.Get("edit_status_dirty")) && undo.EditorBox(left: false).Text == "one\ntwo\n",
                $"Shift+Cmd+Z puts it again (shows '{undo.StatusText}'; the right side reads '{RightText(undo).Replace("\n", "|")}')");

            // 把一行复制到对侧(右键菜单的动作)。行是右键点的那一行,不必先选中它。先再撤销一次:两侧重新不同。
            Press(undo, PhysicalKey.Z, RawInputModifiers.Control);
            check(RightText(undo) == "one\nTWO\n", $"undone once more, the right side is as on disk ('{RightText(undo).Replace("\n", "|")}')");
            var view = undo.GetVisualDescendants().OfType<AlignedRowsView>().Single();
            string[] layout = view.Rows.Select(row => $"{row.Left?.Text ?? "—"}|{row.Right?.Text ?? "—"}").ToArray();
            int leftTwo = Array.FindIndex(view.Rows.ToArray(), row => row.Left?.Text == "two");
            check(layout.Length == 3 && leftTwo >= 0 && view.Rows[leftTwo].Right is null && view.PrimarySelected < 0, $"the sample aligns as three rows, the left 'two' on a row of its own ({string.Join(", ", layout)}; selected {view.PrimarySelected})");
            undo.CopyToSide(view.Rows[leftTwo], fromLeft: true); Pump(undo, 400);
            // 左侧这一行在对侧没有对应行:按对齐它作为新行接在右侧已有的行之后。
            check(RightText(undo) == "one\nTWO\ntwo\n",
                $"copying the left line that has no counterpart on the right adds it after the right's own lines (rows {string.Join(", ", layout)}; the right side reads '{RightText(undo).Replace("\n", "|")}'; status '{undo.StatusText}')");
            check(undo.StatusText.Contains(Lang.Get("md_copied_rows_realign", 1, Lang.Get("side_right"))), $"one line was copied (shows '{undo.StatusText}')");

            // ───────── 改动是在别处做的,编辑页必须跟上 ─────────
            // 回归:差异页上的修改(就地编辑、复制到对侧、右键工具)只改了缓冲区,编辑页的文本框还是旧的;
            // 用户随后在编辑页再敲一个字,旧文本加这个字被整篇写回,差异页上的修改就没了。
            Put("L.txt", "one\ntwo\n"); Put("R.txt", "one\nTWO\n");
            var sync = Open(["--plugins", pluginsPath, "--left", Read("L.txt"), "--right", Read("R.txt"), "--auto-compare"], check, TextRowsShown, "file compare");
            var syncView = sync.GetVisualDescendants().OfType<AlignedRowsView>().Single();
            int leftLine = Array.FindIndex(syncView.Rows.ToArray(), row => row.Left?.Text == "two");
            EditRow(sync, leftLine, true, "TWO", check);      // fixed inline on the diff tab
            check(sync.EditorBox(left: true).Text == "one\nTWO\n", $"an inline edit on the diff tab reaches the edit tab's box (it reads '{(sync.EditorBox(left: true).Text ?? "").Replace("\n", "|")}')");
            // and typing there afterwards keeps the inline fix instead of writing the old text back
            sync.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1; Pump(sync);
            sync.EditorBox(left: true).Text += "three\n"; Pump(sync, 400);
            check(sync.EditorBox(left: true).Text == "one\nTWO\nthree\n" && sync.EditorBox(left: false).Text == "one\nTWO\n",
                $"typing on the edit tab afterwards keeps the fix made on the diff tab (left '{(sync.EditorBox(left: true).Text ?? "").Replace("\n", "|")}')");
            sync.Close();

            // ───────── 正在粘贴的文本框不被同步覆盖 ─────────
            // 实测:键盘输入的 TextChanged 是同步的,没有窗口;粘贴这类内部赋值则要等下一轮调度才提交。在那之前,
            // 别处触发的同步若把缓冲区的旧文本写回,粘贴进来的内容就没了。文本框收到按键(⌘V)之后、提交之前,
            // 把左侧的一行复制到右侧 —— 左框里的内容必须留下。
            Put("L.txt", "one\ntwo\n"); Put("R.txt", "one\nTWO\n");
            var typing = Open(["--plugins", pluginsPath, "--left", Read("L.txt"), "--right", Read("R.txt"), "--auto-compare"], check, TextRowsShown, "file compare");
            var typingView = typing.GetVisualDescendants().OfType<AlignedRowsView>().Single();
            int leftLineOfTwo = Array.FindIndex(typingView.Rows.ToArray(), row => row.Left?.Text == "two");
            check(leftLineOfTwo >= 0, "the left 'two' is on some row");
            typing.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1; Pump(typing);
            var pastedInto = typing.EditorBox(left: true);
            pastedInto.Focus(); pastedInto.CaretIndex = pastedInto.Text?.Length ?? 0; Pump(typing, 100);
            int caret = pastedInto.CaretIndex;
            HeadlessWindowExtensions.KeyPressQwerty(typing, PhysicalKey.V, RawInputModifiers.Control);   // the paste key
            pastedInto.Text += "!";                     // what a paste does: the text changes, the commit comes later
            typing.CopyToSide(typingView.Rows[leftLineOfTwo], fromLeft: true);   // changes the right buffer, and syncs the boxes
            Pump(typing, 400);
            check(pastedInto.Text == "one\ntwo\n!" && typing.SessionText.Left == "one\ntwo\n!",
                $"text pasted on the edit tab survives a sync that runs before its change is committed (box '{(pastedInto.Text ?? "").Replace("\n", "|")}', buffer '{(typing.SessionText.Left ?? "").Replace("\n", "|")}')");
            check(typing.SessionText.Right == "one\nTWO\ntwo\n" && typing.EditorBox(left: false).Text == "one\nTWO\ntwo\n",
                $"while the copy to the other side does reach the right box ('{(typing.SessionText.Right ?? "").Replace("\n", "|")}')");
            // 那次输入提交之后,保护必须解除:之后差异页上的修改照常同步到这个文本框。
            typing.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 0; Pump(typing, 100);
            EditRow(typing, Array.FindIndex(typingView.Rows.ToArray(), row => row.Left?.Text == "one"), true, "ONE", check);
            check(typing.EditorBox(left: true).Text == "ONE\ntwo\n!" && typing.SessionText.Left == "ONE\ntwo\n!",
                $"once the pasted change is committed the box follows the buffer again (box '{(typing.EditorBox(left: true).Text ?? "").Replace("\n", "|")}', buffer '{(typing.SessionText.Left ?? "").Replace("\n", "|")}')");
            typing.Close();

            // ───────── the hex view: ⌘S writes the edited bytes ─────────
            string binA = Read("A.bin"), binB = Read("B.bin");
            File.WriteAllBytes(binA, [0x00, 0xFF, 0x00, 0x01]); File.WriteAllBytes(binB, [0x00, 0xFF, 0x00, 0x02]);
            var hex = Open(["--plugins", pluginsPath, "--left", binA, "--right", binB, "--auto-compare"], check, w => w.HexViewActive, "binary compare");
            EditRow(hex, 0, true, "00 FF 00 02", check);
            check(File.ReadAllBytes(binA).SequenceEqual(new byte[] { 0x00, 0xFF, 0x00, 0x01 }), "the edit is in the view only; the file on disk is as it was");
            Press(hex, PhysicalKey.S, RawInputModifiers.Control);
            check(File.ReadAllBytes(binA).SequenceEqual(new byte[] { 0x00, 0xFF, 0x00, 0x02 }) && File.ReadAllBytes(binB).SequenceEqual(new byte[] { 0x00, 0xFF, 0x00, 0x02 }),
                $"Cmd+S in the hex view writes the left file's edited bytes and nothing else ({Hex(binA)}, {Hex(binB)})");
            hex.Close();

            // ───────── files dropped on the window ─────────
            string first = Read("dropped-1.txt"), second = Read("dropped-2.txt");
            Put("dropped-1.txt", "a\n"); Put("dropped-2.txt", "b\n");
            var drop = new MainWindow(["--plugins", pluginsPath]);
            drop.Show(); Pump(drop);
            check(string.IsNullOrEmpty(drop.PathBarTexts.Left) && string.IsNullOrEmpty(drop.PathBarTexts.Right), $"the path boxes start empty ({drop.PathBarTexts.Left} ↔ {drop.PathBarTexts.Right})");
            Drop(drop, new Point(300, 300), Files(first, second)); Pump(drop);
            check(drop.PathBarTexts == (first, second) && drop.PathBarBoxes[0].Text == first, $"two files dropped fill the left and the right path ({drop.PathBarTexts.Left} ↔ {drop.PathBarTexts.Right})");
            check(!TextRowsShown(drop) && drop.FolderRows.Count == 0, "dropping fills the paths in; it does not start a compare");
            Drop(drop, new Point(drop.Bounds.Width * 0.75, 300), Files(second)); Pump(drop);
            check(drop.PathBarTexts == (first, second) && drop.PathBarTexts.Right == second, "one file dropped on the right half fills the right path only");
            Drop(drop, new Point(drop.Bounds.Width * 0.25, 300), Files(first)); Pump(drop);
            check(drop.PathBarTexts == (first, second), "one file dropped on the left half fills the left path only");
            var text = new DataTransfer(); text.Add(DataTransferItem.CreateText("not a file"));
            Drop(drop, new Point(300, 300), text); Pump(drop);
            check(drop.PathBarTexts == (first, second), "a drop that carries no file changes nothing");
            // and Enter on a path box compares what was dropped
            Drop(drop, new Point(300, 300), Files(Read("L.txt"), Read("R.txt"))); Pump(drop);
            check(drop.PathBarTexts == (Read("L.txt"), Read("R.txt")) && drop.CommitPathBars(), "the dropped pair can be compared from the path bar");
            var dropped = DateTime.UtcNow.AddSeconds(20);
            while (!TextRowsShown(drop) && DateTime.UtcNow < dropped) Pump(drop, 20);
            check(TextRowsShown(drop), "dropping two text files and pressing Enter compares them");
            drop.Close();
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.WriteLine("shortcut test files await process exit: " + root); }
        }
    }
}
