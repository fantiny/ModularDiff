using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ModularDiff.Contracts;
using ModularDiff.Core;
using ModularDiff.Desktop;

/// <summary>macOS 界面在非中文语言下不再夹杂自己的文字。源码里的硬编码由守卫测试(tests/ModularDiff.Tests)拦住;
/// 这里核对运行时:从英语启动的窗口、切换语言之后的状态栏与界面、操作后的提示、合并窗口 —— 都按所选语言显示。</summary>
internal static class LocalizationTests
{
    private static void Pump(Window window, int milliseconds = 150)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Thread.Sleep(10); }
    }

    /// <summary>取回一个任务的结果而不脱离 UI 线程(无头环境里不能 await 真实异步 IO,后续体会落到线程池线程上)。</summary>
    private static T OnUi<T>(Window window, Task<T> task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) Pump(window, 10);
        Pump(window, 30);
        return task.GetAwaiter().GetResult();
    }
    private static void OnUi(Window window, Task task) => OnUi(window, ContinueWith(task));
    private static async Task<bool> ContinueWith(Task task) { await task.ConfigureAwait(false); return true; }

    private static T ById<T>(Visual root, string id) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetAutomationId(control) == id);

    /// <summary>汉字、假名、谚文。英文界面里一个都不该出现;语言下拉框除外 —— 每种语言用它自己的文字命名自己。</summary>
    private static bool FarEast(string? text) => text is not null && text.Any(c => c is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff' or >= '\uac00' and <= '\ud7af');

    /// <summary>窗口显示的全部文字:文本块、提示、输入框水印、下拉框的条目(语言下拉框除外)。</summary>
    private static string[] ShownText(Window window)
    {
        var controls = window.GetVisualDescendants().OfType<Control>().Where(c => AutomationProperties.GetAutomationId(c) != "md_tip_lang").ToArray();
        IEnumerable<string?> texts = controls.OfType<TextBlock>().Select(t => t.Text);
        texts = texts.Concat(controls.Select(c => ToolTip.GetTip(c)?.ToString())).Concat(controls.OfType<TextBox>().Select(t => t.Watermark))
            .Concat(controls.OfType<ComboBox>().SelectMany(c => c.Items.Cast<object?>().Select(item => item?.ToString())));
        return texts.Where(t => !string.IsNullOrEmpty(t)).Select(t => t!).ToArray();
    }

    private static string[] Foreign(Window window) => ShownText(window).Where(FarEast).ToArray();

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

    public static void Run(string pluginsPath, PluginCatalog plugins, Action<bool, string> check)
    {
        check(Dispatcher.UIThread.CheckAccess() && SynchronizationContext.Current is not null, "localization tests create windows: they need the UI thread and its synchronization context");
        string root = Path.Combine(Path.GetTempPath(), "ModularDiff-l10n-" + Guid.NewGuid().ToString("N"));
        string left = Path.Combine(root, "L"), right = Path.Combine(root, "R"), outDir = Path.Combine(root, "out");
        void Put(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
        Put(Path.Combine(left, "same.txt"), "a\n"); Put(Path.Combine(right, "same.txt"), "a\n");
        Put(Path.Combine(left, "diff.txt"), "one\ntwo\n"); Put(Path.Combine(right, "diff.txt"), "one\nTWO\n");
        Put(Path.Combine(left, "only-left.txt"), "left only\n"); Put(Path.Combine(right, Path.Combine("sub", "nested.txt")), "nested\n");
        Put(Path.Combine(root, "base.txt"), "common\nkeep\n"); Put(Path.Combine(root, "local.txt"), "common\nmine\n"); Put(Path.Combine(root, "remote.txt"), "common\ntheirs\n");
        Directory.CreateDirectory(outDir);
        // 窗口构造时应用的是已保存的语言(用户的持久化偏好),所以设定语言要像语言下拉框那样:保存 + 应用。
        void Use(string language) { Palette.SaveLanguage(language); Lang.Apply(language); }
        try
        {
            // ───────── 从英语启动:主窗口里没有一处自己的文字 ─────────
            Use("English");
            var english = Open(["--plugins", pluginsPath, "--left", left, "--right", right, "--auto-compare"], check, w => w.FolderCompareComplete, "folder compare in English");
            check(english.Title == "ModularDiff · Cross-platform", $"the title comes from the pack (shows '{english.Title}')");
            check(english.PathBarBoxes.Select(box => box.Watermark).SequenceEqual(["Left file or folder", "Right file or folder"]),
                $"the path boxes' hints are in English (shows {string.Join(" | ", english.PathBarBoxes.Select(box => box.Watermark))})");
            string[] foreign = Foreign(english);
            check(foreign.Length == 0, $"the English UI shows text in a Far-East script: {string.Join(" | ", foreign.Take(10))}");
            check(english.StatusText == Lang.Get("status_summary", 4, 1, 3, 0) && english.StatusText == "4 files · 1 identical · 3 different · 0 errors",
                $"the folder summary is English, and formatted with its numbers (shows '{english.StatusText}')");

            // 编辑页的侧别标题与按钮提示
            var tabs = english.GetVisualDescendants().OfType<TabControl>().Single();
            tabs.SelectedIndex = 1; Pump(english);
            string[] editTexts = ShownText(english);
            check(editTexts.Contains("left") && editTexts.Contains("right") && editTexts.Contains("Undo edits on this side") && editTexts.Contains("Redo edits on this side"),
                $"the edit tab names the sides and its buttons in English (shows {string.Join(" | ", editTexts.Where(t => t.Length < 60).Distinct().Take(14))})");
            foreign = Foreign(english);
            check(foreign.Length == 0, $"the English edit tab shows text in a Far-East script: {string.Join(" | ", foreign.Take(10))}");
            tabs.SelectedIndex = 2; Pump(english);
            string report = Path.Combine(outDir, "report.html");
            check(OnUi(english, english.ExportReportAsync(report)) && english.StatusText == $"Report saved: {Path.GetFullPath(report)}",
                $"a message after an action is English too (shows '{english.StatusText}')");
            english.Close();

            // ───────── 文本对,连切两次语言:状态栏、水印、编辑页都跟着走 ─────────
            string pairLeft = Path.Combine(left, "diff.txt"), pairRight = Path.Combine(right, "diff.txt");
            var pair = Open(["--plugins", pluginsPath, "--left", pairLeft, "--right", pairRight, "--auto-compare"], check, TextRowsShown, "file compare in English");
            check(pair.StatusText == $"Saved · 1 difference block(s) · {pairLeft} ↔ {pairRight}", $"the text session's status in English (shows '{pair.StatusText}')");
            var languageBox = ById<ComboBox>(pair, "md_tip_lang");
            languageBox.SelectedItem = "日本語"; Pump(pair, 400);
            check(pair.StatusText == Lang.Get("md_edit_status", Lang.Get("edit_status_saved"), 1, pairLeft, pairRight) && pair.StatusText.StartsWith("保存済み", StringComparison.Ordinal),
                $"the status line is rendered again in the language chosen (shows '{pair.StatusText}')");
            check(pair.Title == Lang.Get("md_title") && pair.PathBarBoxes.Select(box => box.Watermark).SequenceEqual([Lang.Get("md_path_left_hint"), Lang.Get("md_path_right_hint")]),
                $"a language switch reaches the title and the path boxes' hints (shows '{pair.Title}', {string.Join(" | ", pair.PathBarBoxes.Select(box => box.Watermark))})");
            var pairTabs = pair.GetVisualDescendants().OfType<TabControl>().Single();
            pairTabs.SelectedIndex = 1; Pump(pair);
            string[] editTextsJa = ShownText(pair);
            check(editTextsJa.Contains(Lang.Get("side_left")) && editTextsJa.Contains(Lang.Get("side_right")) && editTextsJa.Contains(Lang.Get("md_undo_side")) && editTextsJa.Contains(Lang.Get("md_redo_side")),
                $"the edit tab's captions and tips follow a language switch (shows {string.Join(" | ", editTextsJa.Where(t => t.Length < 60).Distinct().Take(14))})");
            foreign = editTextsJa.Where(FarEast).ToArray();
            check(!foreign.Any(t => t.Contains("左侧") || t.Contains("右侧") || t.Contains("编辑")), $"nothing is left over from the Chinese UI: {string.Join(" | ", foreign.Where(t => t.Contains("左侧") || t.Contains("右侧") || t.Contains("编辑")).Take(6))}");

            // 未保存的修改:状态栏说明;导出报告时也说明
            pairTabs.SelectedIndex = 0; Pump(pair);
            pair.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); pair.UpdateLayout();
            pair.BeginEditRow(0, true);
            var cellEditor = pair.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "CellEditor" && t.IsVisible);
            check(cellEditor is not null, "inline cell editor realized");
            cellEditor!.Text += " edited";
            Dispatcher.UIThread.RunJobs(); Pump(pair);
            // 编辑之后对齐没重算:状态栏照 WPF 的说法写"未对齐 · ⟳ 重算",这句话也来自语言包。
            check(pair.StatusText == Lang.Get("md_edit_status_unaligned", Lang.Get("edit_status_dirty"), Lang.Get("hunks_unaligned"), pairLeft, pairRight),
                $"unsaved edits and the unaligned hint show in the status line, in the UI language (shows '{pair.StatusText}')");
            string pairReport = Path.Combine(outDir, "pair.txt");
            OnUi(pair, pair.ExportReportAsync(pairReport, plugins.Get<IResultFormatter>("text")));
            check(pair.StatusText == Lang.Get("status_export_saved", Path.GetFullPath(pairReport)) + " · " + Lang.Get("md_export_unsaved"),
                $"the export's status is composed in the UI language (shows '{pair.StatusText}')");
            pair.Close();

            // ───────── 合并窗口:此前在任何语言下都是中文 ─────────
            Use("English");
            var merge = new MergeWindow(Path.Combine(root, "base.txt"), Path.Combine(root, "local.txt"), Path.Combine(root, "remote.txt"), plugins);
            merge.Show();
            var merged = DateTime.UtcNow.AddSeconds(20);
            while (merge.ConflictCount != 1 && DateTime.UtcNow < merged) Pump(merge, 20);
            check(merge.ConflictCount == 1 && merge.StatusText == "Merge complete: 1 conflict(s). ↑↓ to move, ⇐ take mine, ⇒ take theirs.",
                $"the merge window reports in English (shows '{merge.StatusText}')");
            check(merge.Title == "Three-way Merge — ModularDiff" && merge.PathBoxes.Select(box => box.Watermark).SequenceEqual(["Base (common ancestor)", "Left (mine) — LOCAL", "Right (theirs) — REMOTE"]),
                $"the merge window's title and path hints are English (shows '{merge.Title}', {string.Join(" | ", merge.PathBoxes.Select(box => box.Watermark))})");
            foreign = Foreign(merge);
            check(foreign.Length == 0, $"the English merge window shows text in a Far-East script: {string.Join(" | ", foreign.Take(10))}");
            merge.Take(useRight: false);
            check(merge.StatusText == $"Took {Lang.Get("side_left")}; 0 conflict(s) remain.", $"taking a side reports in English (shows '{merge.StatusText}')");
            merge.Take(useRight: false);
            check(merge.StatusText == "Select a conflict with ↑↓ first.", $"a refusal reports in English (shows '{merge.StatusText}')");
            var noFiles = new MergeWindow(plugins: plugins);
            noFiles.Show(); Pump(noFiles);
            check(noFiles.StatusText == Lang.Get("md_merge_start_hint") && noFiles.StatusText.StartsWith("Fill in the three file paths", StringComparison.Ordinal),
                $"the merge window's opening hint is English (shows '{noFiles.StatusText}')");
            OnUi(noFiles, noFiles.MergeAsync());
            check(noFiles.StatusText == "All three paths must be existing files.", $"a failed merge reports in English (shows '{noFiles.StatusText}')");
            noFiles.Close(); merge.Close();
        }
        finally
        {
            Use("中文");
            try { Directory.Delete(root, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.WriteLine("localization test files await process exit: " + root); }
        }
    }
}
