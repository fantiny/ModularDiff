using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ModularDiff.Contracts;
using ModularDiff.Core;
using ModularDiff.Desktop;

/// <summary>目录同步预览:每个文件一行,复制方向逐项可改。此前 macOS 只能把计划列成文字、按插件建议的方向整体执行 ——
/// 用户无法对单个文件说"这个不要动"(WPF 的同步预览一直可以)。执行必须按预览里的决定,而不是按建议。</summary>
internal static class SyncPreviewTests
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

    private static bool Raw(string text) => Regex.IsMatch(text, @"\b(sync|file_status)_[a-z_]+");

    private static string[] ShownText(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)
            .Concat(window.GetVisualDescendants().OfType<ComboBox>().SelectMany(box => box.Items.Cast<string>()))
            .Where(t => !string.IsNullOrEmpty(t)).Select(t => t!).ToArray();

    public static void Run(string pluginsPath, PluginCatalog plugins, Action<bool, string> check)
    {
        check(Dispatcher.UIThread.CheckAccess() && SynchronizationContext.Current is not null, "sync preview tests create windows: they need the UI thread and its synchronization context");
        string root = Path.Combine(Path.GetTempPath(), "ModularDiff-syncpreview-" + Guid.NewGuid().ToString("N"));
        string left = Path.Combine(root, "L"), right = Path.Combine(root, "R");
        void Put(string side, string relative, string text) { string path = Path.Combine(side, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
        Put(left, "same.txt", "s"); Put(right, "same.txt", "s");
        Put(left, "diff.txt", "1"); Put(right, "diff.txt", "2");
        Put(left, Path.Combine("onlyL", "z.txt"), "z");
        Put(right, Path.Combine("onlyR", "w.txt"), "w");
        Put(left, Path.Combine("sub", "nested.txt"), "n"); Put(right, Path.Combine("sub", "nested.txt"), "m");
        string nested = Path.Combine("sub", "nested.txt"), onlyLeft = Path.Combine("onlyL", "z.txt"), onlyRight = Path.Combine("onlyR", "w.txt");
        try
        {
            // ───────── 计划:左 → 右,未选择即全部有差异的文件 ─────────
            var folder = new MainWindow(["--plugins", pluginsPath, "--left", left, "--right", right, "--auto-compare"]);
            folder.Show();
            var ready = DateTime.UtcNow.AddSeconds(20);
            while (!folder.FolderCompareComplete && DateTime.UtcNow < ready) Pump(folder, 20);
            check(folder.FolderCompareComplete, $"the folder compare did not finish (status: {folder.StatusText})");
            string[] targets = folder.SyncTargetPaths();
            check(targets.Length == 4, $"nothing selected means every differing file: 4 expected, got {string.Join(", ", targets)}");
            var plan = OnUi(folder, plugins.Get<ISyncPlanner>("sync-plan").PlanAsync(left, right, targets, SyncMode.LeftToRight));
            IReadOnlyList<SyncPreviewItem> preview = plugins.Get<ISyncPreview>("sync-preview").Describe(plan);

            // ───────── 一行一个文件:默认是插件建议的动作,跳过总是可选 ─────────
            var chooser = new SyncPreviewWindow(plan, preview);
            chooser.Show(); Pump(chooser);
            check(chooser.Rows.Count == preview.Count && chooser.Rows.Select(row => row.RelativePath).Order().SequenceEqual(targets.Order()),
                $"one row per file of the plan: {string.Join(", ", chooser.Rows.Select(row => row.RelativePath))}");
            check(chooser.Rows.All(row => row.Action == preview.Single(item => item.RelativePath == row.RelativePath).SuggestedAction), "each row starts on the action the plugin suggests");
            check(chooser.Rows.All(row => row.Choices.Any(choice => choice.Action == SyncAction.Skip)), "skip is offered for every file");
            check(chooser.ActionOf("diff.txt") == SyncAction.LeftToRight && chooser.ActionOf(onlyRight) == SyncAction.Skip,
                "left → right copies the two files that exist on the left and leaves the one that exists on the right only");
            check(chooser.CanRun && chooser.SummaryText == Lang.Get("sync_summary", 4, 1, 2, 1),
                $"the summary counts what would happen: 4 items, 1 new, 2 overwrites, 1 skip (shows '{chooser.SummaryText}')");

            // ───────── 逐项改方向 ─────────
            check(chooser.Choose("diff.txt", SyncAction.Skip) && chooser.ActionOf("diff.txt") == SyncAction.Skip, "a file can be left alone");
            check(chooser.SummaryText == Lang.Get("sync_summary", 4, 1, 1, 2) && chooser.CanRun, $"the summary follows the change (shows '{chooser.SummaryText}')");
            check(!chooser.Choose(onlyRight, SyncAction.LeftToRight) && chooser.ActionOf(onlyRight) == SyncAction.Skip,
                "an action the plugin does not offer for a file is refused, and the file keeps its action");
            check(!chooser.Choose("no-such.txt", SyncAction.Skip), "a file that is not in the plan is refused");
            SyncDecision[] expected =
            [
                new("diff.txt", SyncAction.Skip), new(onlyLeft, SyncAction.LeftToRight), new(onlyRight, SyncAction.Skip), new(nested, SyncAction.LeftToRight),
            ];
            IReadOnlyList<SyncDecision> decisions = chooser.Decisions;
            check(decisions.SequenceEqual(expected), $"the decisions are the rows as they stand now: {string.Join("; ", decisions.Select(d => $"{d.RelativePath}={d.Action}"))}");
            // 真实的下拉框也走同一条路:选中第一项(跳过)即改掉那一行的动作
            var combos = chooser.GetVisualDescendants().OfType<ComboBox>().ToArray();
            check(combos.Length == chooser.Rows.Count, $"one list per row ({combos.Length} of {chooser.Rows.Count})");
            int nestedIndex = Array.FindIndex(chooser.Rows.ToArray(), row => row.RelativePath == nested);
            combos[nestedIndex].SelectedIndex = Array.FindIndex(chooser.Rows[nestedIndex].Choices.ToArray(), choice => choice.Action == SyncAction.Skip);
            Pump(chooser, 100);
            check(chooser.ActionOf(nested) == SyncAction.Skip, "picking a caption in a row's list changes that file's action");
            // 全部跳过:没有可执行的动作,"执行"不可用,汇总如实说
            var allSkipped = new SyncPreviewWindow(plan, preview);
            allSkipped.Show(); Pump(allSkipped);
            check(allSkipped.Rows.All(row => allSkipped.Choose(row.RelativePath, SyncAction.Skip)), "every file can be set to skip");
            check(!allSkipped.CanRun && allSkipped.SummaryText == Lang.Get("sync_summary", 4, 0, 0, 4),
                $"with nothing to copy there is nothing to run: the button is disabled and the summary says so (shows '{allSkipped.SummaryText}', can run = {allSkipped.CanRun})");
            var runButton = allSkipped.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, Lang.Get("md_folder_sync_run")));
            check(!runButton.IsEnabled, "the Run button itself is disabled when every file is skipped");
            allSkipped.Close();
            chooser.Choose(nested, SyncAction.LeftToRight);

            // ───────── 执行按决定,而不是按建议 ─────────
            SyncResult result = OnUi(folder, folder.RunSyncAsync(plan, decisions));
            check(File.ReadAllText(Path.Combine(right, "diff.txt")) == "2", "the file set to skip was not touched (it still differs from the left)");
            check(File.ReadAllText(Path.Combine(right, "onlyL", "z.txt")) == "z" && File.ReadAllText(Path.Combine(right, "sub", "nested.txt")) == "n",
                "the files left on their suggested action were copied to the right");
            check(!File.Exists(Path.Combine(left, "onlyR", "w.txt")) && File.Exists(Path.Combine(right, "onlyR", "w.txt")), "a file that exists on the right only is not copied left in a left → right sync");
            check(result.Entries.Count == 4 && result.Entries.Count(entry => entry.Outcome == SyncOutcome.Copied) == 2
                && result.Entries.Single(entry => entry.RelativePath == "diff.txt").Outcome == SyncOutcome.Skipped,
                $"the result says what happened: 2 copied, the skipped one skipped ({string.Join(", ", result.Entries.Select(entry => $"{entry.RelativePath}:{entry.Outcome}"))})");
            check(folder.StatusText.Contains(Lang.Get("status_sync_done", 2, 0, 0)), $"the status line reports the sync as done for the decisions (shows '{folder.StatusText}')");
            chooser.Close();

            // ───────── 换语言:标题、列名、动作名、状态都跟着走,没有原始键 ─────────
            foreach (string pack in new[] { "English", "日本語" })
            {
                Lang.Apply(pack);
                var localized = new SyncPreviewWindow(plan, preview);
                localized.Show(); Pump(localized);
                string[] shown = ShownText(localized);
                check(localized.Title == Lang.Get("md_folder_sync_preview_title"), $"{pack}: the title comes from the pack (shows '{localized.Title}')");
                check(shown.Contains(Lang.Get("sync_col_action")) && shown.Contains(Lang.Get("sync_col_relpath")) && shown.Contains(Lang.Get("sync_choice_skip"))
                    && shown.Contains(Lang.Get("sync_choice_overwrite_right")) && shown.Contains(Lang.Get("md_folder_sync_run")),
                    $"{pack}: column captions, action names and the button are in the UI language (shows {string.Join(" | ", shown.Where(t => t.Length < 40).Distinct().Take(16))})");
                check(!shown.Any(Raw) && localized.Rows.All(row => !Raw(row.State) && !Raw(row.LeftInfo) && !Raw(row.RightInfo)),
                    $"{pack}: a raw key would be shown (rows: {string.Join(" | ", localized.Rows.Select(row => $"{row.State} / {row.LeftInfo} / {row.RightInfo}"))})");
                check(pack != "English" || (shown.Contains("Skip") && shown.Contains("Relative path") && localized.Rows.All(row => row.State is "Different" or "Left only" or "Right only")),
                    $"English: the rows read as the user should read them (rows: {string.Join(" | ", localized.Rows.Select(row => row.State))})");
                localized.Close();
            }
        }
        finally
        {
            Lang.Apply("中文");
            try { Directory.Delete(root, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.WriteLine("sync preview test files await process exit: " + root); }
        }
    }
}
