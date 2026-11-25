using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ModularDiff.Contracts;
using ModularDiff.Core;
using ModularDiff.Desktop;

/// <summary>macOS 目录视图与同步的契约测试。
/// 背景:目录模块改为渐进式扫描、结果里出现目录条目与 Pending 之后,Desktop 既没有应用增量,
/// 同步预览又开始显示插件发出的原始语言键,不选文件时还把目录也纳入同步计划 —— 全都没有测试覆盖。</summary>
internal static class FolderViewTests
{
    private static void Pump(Window window, int milliseconds = 250)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Thread.Sleep(10); }
    }

    private static MainWindow OpenFolder(string pluginsPath, string left, string right, Action<bool, string> check, string what)
    {
        var window = new MainWindow(["--plugins", pluginsPath, "--left", left, "--right", right, "--auto-compare"]);
        window.Show();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!window.FolderCompareComplete && DateTime.UtcNow < deadline) Pump(window, 20);
        check(window.FolderCompareComplete, $"{what}: the folder compare did not finish (status: {window.StatusText})");
        return window;
    }

    /// <summary>取回一个任务的结果而不脱离 UI 线程:这里不能 await —— 无头环境里 await 真实异步 IO 之后,
    /// 后续体会落在线程池线程上,再碰控件就是 "Call from invalid thread"。</summary>
    private static T OnUi<T>(Window window, Task<T> task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) Pump(window, 10);
        Pump(window, 30);
        return task.GetAwaiter().GetResult();   // 未完成或已失败:在这里抛出
    }

    private static void OnUi(Window window, Task task) => OnUi(window, ContinueWith(task));

    private static async Task<bool> ContinueWith(Task task) { await task.ConfigureAwait(false); return true; }

    private static FileComparison Entry(string path, FileStatus status, bool directory = false, string? note = null) =>
        new(path, status, [], IsDirectory: directory, Note: note);

    private static bool LooksRaw(string text) =>
        Regex.IsMatch(text, @"\b(sync|file_status)_[a-z_]+") || Regex.IsMatch(text, @"(^|\s)(LeftToRight|RightToLeft|Skip|Modified|LeftOnly|RightOnly|Equal|Pending|Error|Copied|Failed|Skipped|Cancelled|Completed)(\s|$)");

    public static void Run(string pluginsPath, PluginCatalog plugins, Action<bool, string> check)
    {
        // ───────── 工具栏在默认窗口尺寸下不得被裁切 ─────────
        // 存量缺陷:工具栏 25 个控件共需约 1340px,默认窗口只给得了 1230px —— 最右侧的按钮被窗口边缘切掉,
        // 中间的下拉框被挤成空白(用户截图里"算法下拉框是空的"就有它一份)。
        {
            var sized = new MainWindow(["--plugins", pluginsPath]);   // 不指定尺寸:用默认的 1250x820
            sized.Show(); Pump(sized);
            var bar = sized.GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "MainToolbar");
            var rootLayout = (Grid)sized.Content!;
            var sizeLanguage = sized.GetVisualDescendants().OfType<ComboBox>().Single(c => Avalonia.Automation.AutomationProperties.GetAutomationId(c) == "md_tip_lang");
            foreach (string pack in new[] { "中文", "English", "日本語", "繁體中文" })
            {
                sizeLanguage.SelectedItem = pack; Pump(sized, 300);
                check(bar.Bounds.Right <= rootLayout.Bounds.Width + 0.5 && bar.Bounds.Bottom <= rootLayout.Bounds.Height,
                    $"{pack}: the toolbar itself runs past the window (toolbar {bar.Bounds}, content {rootLayout.Bounds})");
                foreach (var control in bar.Children)
                    check(control.Bounds.Left >= -0.5 && control.Bounds.Right <= bar.Bounds.Width + 0.5 && control.Bounds.Bottom <= bar.Bounds.Height + 0.5,
                        $"{pack}: toolbar control is clipped at the window edge: {control.GetType().Name} '{control.GetType().GetProperty("Content")?.GetValue(control)}' at {control.Bounds}, toolbar {bar.Bounds}");
                var algorithm = sized.GetVisualDescendants().OfType<ComboBox>().Single(c => Avalonia.Automation.AutomationProperties.GetAutomationId(c) == "md_tip_algo");
                check(algorithm.Bounds.Width >= 90 && Equals(algorithm.SelectedItem, "myers"), $"{pack}: the algorithm box is squeezed ({algorithm.Bounds.Width:0}px) or lost its selection");
            }
            sizeLanguage.SelectedItem = "中文"; Pump(sized, 200);
            sized.Close();
        }

        // ───────── 状态栏的配方规则 ─────────
        // 用语言键生成的状态文本带"配方",切语言时按新语言重新生成;直接赋值的文本必须让旧配方失效,
        // 否则下次切语言会把一条早已过期的消息(比如上一次的目录汇总)重新渲染出来,顶掉当前状态。
        var line = new MainWindow.StatusLine(new TextBlock());
        string language = "zh";
        line.Localized(() => $"summary in {language}");
        check(line.Text == "summary in zh", "a localized status renders from its recipe");
        language = "en"; line.Relocalize();
        check(line.Text == "summary in en", "a language switch re-renders a localized status");
        line.Text = "plain message";
        check(line.Text == "plain message" && !line.HasRecipe, "a plain assignment invalidates the recipe");
        language = "ja"; line.Relocalize();
        check(line.Text == "plain message", "a plain message must survive a language switch instead of being replaced by a stale recipe");
        line.Relocalize();
        check(line.Text == "plain message", "re-localizing without a recipe is a no-op");
        line.Localized(() => "again");
        check(line.Text == "again" && line.HasRecipe, "a new recipe takes over");

        check(Dispatcher.UIThread.CheckAccess(), "folder tests create windows, so they must be started on the UI thread");
        check(SynchronizationContext.Current is not null, "folder tests need the Avalonia synchronization context: with it cleared, a window's awaited work resumes on a thread-pool thread and UI access throws. Insert the call before the test program clears the context.");
        string originalDirectory = Environment.CurrentDirectory;
        string root = Path.Combine(Path.GetTempPath(), "ModularDiff-folderview-" + Guid.NewGuid().ToString("N"));
        string left = Path.Combine(root, "L"), right = Path.Combine(root, "R");
        void Put(string side, string relative, string text)
        {
            string path = Path.Combine(side, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Put(left, "same.txt", "s"); Put(right, "same.txt", "s");
        Put(left, "diff.txt", "1"); Put(right, "diff.txt", "2");
        Put(left, Path.Combine("sub", "nested.txt"), "n"); Put(right, Path.Combine("sub", "nested.txt"), "m");
        Put(left, Path.Combine("onlyL", "z.txt"), "z");
        Put(right, Path.Combine("onlyR", "w.txt"), "w");
        Directory.CreateDirectory(Path.Combine(left, "emptyBoth")); Directory.CreateDirectory(Path.Combine(right, "emptyBoth"));

        try
        {
            // ───────── 同步 ─────────
            var sync = OpenFolder(pluginsPath, left, right, check, "sync");
            sync.SelectFolderFiles();   // 清空选择:"不选 = 同步全部差异"
            string[] targets = sync.SyncTargetPaths();
            check(targets.Length == 4 && targets.Contains("diff.txt") && targets.Contains(Path.Combine("sub", "nested.txt"))
                    && targets.Contains(Path.Combine("onlyL", "z.txt")) && targets.Contains(Path.Combine("onlyR", "w.txt")),
                $"sync scope should be exactly the four differing files, got: {string.Join(", ", targets)}");
            check(!targets.Any(t => t is "sub" or "onlyL" or "onlyR" or "emptyBoth"), $"folders must never be sync targets: {string.Join(", ", targets)}");

            foreach (string pack in new[] { "中文", "English" })
            {
                Lang.Apply(pack);
                var lines = OnUi(sync, sync.DescribeSync(SyncMode.LeftToRight));   // 计划在后台算:泵调度器直到取回
                string all = string.Join("\n", lines);
                check(lines.Count == 4, $"{pack}: preview should list 4 items, got {lines.Count}:\n{all}");
                check(!Regex.IsMatch(all, @"\b(sync|file_status)_[a-z_]+") && (pack != "中文" || !LooksRaw(all)),
                    $"{pack}: sync preview shows raw keys or enum names:\n{all}");
                string diff = lines.First(l => l.Contains("diff.txt"));
                check(diff.Contains(Lang.Get("file_status_modified")) && diff.Contains(Lang.Get("sync_choice_overwrite_right")), $"{pack}: preview should name the state and action in the UI language:\n{diff}");
                string added = lines.First(l => l.Contains("z.txt"));
                check(added.Contains(Lang.Get("file_status_left_only")) && added.Contains(Lang.Get("sync_choice_add_right")) && added.Contains(Lang.Get("sync_stamp_missing")),
                    $"{pack}: one-side file should read as left-only / add to right / missing:\n{added}");
            }

            // 真实执行一次同步(临时目录内),结果与历史的文本同样不能是枚举名。
            Lang.Apply("中文");
            var plan = OnUi(sync, plugins.Get<ISyncPlanner>("sync-plan").PlanAsync(left, right, ["diff.txt"], SyncMode.LeftToRight));
            var preview = plugins.Get<ISyncPreview>("sync-preview");
            var safety = new SyncSafetyContext(Path.Combine(AppPaths.DataDirectory, "SyncHistory"), plugins.Get<ISyncBackup>("sync-backup"), plugins.Get<ISyncJournal>("sync-journal"));
            var executed = OnUi(sync, plugins.Get<ISyncExecutor>("sync-execute").ExecuteAsync(plan, preview.Describe(plan).Select(p => new SyncDecision(p.RelativePath, p.SuggestedAction)).ToArray(), safety));
            foreach (string pack in new[] { "中文", "English" })
            {
                Lang.Apply(pack);
                string resultText = sync.SyncResultSummary(executed) + "\n" + string.Join("\n", sync.DescribeSyncResult(executed));
                check(pack != "中文" || !LooksRaw(resultText), $"sync result shows raw enum names:\n{resultText}");
                check(sync.SyncResultSummary(executed) == Lang.Get("sync_result_summary", 1, 0, 0, 0),
                    $"{pack}: result summary should come from the pack: {sync.SyncResultSummary(executed)} vs {Lang.Get("sync_result_summary", 1, 0, 0, 0)}");
                check(sync.DescribeSyncResult(executed).Count == 1 && sync.DescribeSyncResult(executed)[0].Contains("diff.txt"), "one result line per file");
                check(sync.SyncOperationStateText("Completed") == Lang.Get("sync_op_completed") && sync.SyncOperationStateText("SomethingNew") == "SomethingNew",
                    $"{pack}: history state should resolve through the pack and pass unknown states through");
            }
            Lang.Apply("中文");
            sync.Close();

            // ───────── 目录视图:增量 ─────────
            var delta = new MainWindow(["--plugins", pluginsPath, "--left", left, "--right", right]);   // 不自动比较:手工喂进度
            delta.Show(); Pump(delta);
            var rootWave = new ComparisonResult(left, right, "directories", "myers",
                [Entry("sub", FileStatus.Pending, directory: true, note: "子目录扫描中…"), Entry("same.txt", FileStatus.Pending, note: "内容比较中…"), Entry("diff.txt", FileStatus.Pending, note: "内容比较中…")], []);
            delta.ApplyFolderProgress(new(0, 0, "scan:root", PartialResult: rootWave));
            // 清单按相对路径有序(与发现顺序无关):增量插入时已显示的行不会跳位。
            check(delta.FolderRows.Select(f => f.RelativePath).SequenceEqual(["diff.txt", "same.txt"]), $"root wave should list the two root files in path order (folders present on both sides are structure): {string.Join(", ", delta.FolderRows.Select(f => f.RelativePath))}");
            check(delta.FolderRows.All(f => f.Status == FileStatus.Pending) && delta.StatusText.Contains(Lang.Get("folder_scan_root")), "root wave: rows pending and status bar announces the scan");
            delta.ApplyFolderProgress(new(3, 0, "scan:wave:3", Added: [Entry(Path.Combine("sub", "nested.txt"), FileStatus.Pending, note: "内容比较中…"), Entry("onlyL", FileStatus.LeftOnly, directory: true)]));
            check(delta.FolderRows.Count == 4 && delta.FolderRows.Any(f => f.RelativePath == Path.Combine("sub", "nested.txt")) && delta.FolderRows.Any(f => f.RelativePath == "onlyL"),
                "an Added delta should grow the list, including a folder that exists on one side only");
            delta.ApplyFolderProgress(new(1, 3, "diff.txt", FileUpdate: Entry("diff.txt", FileStatus.Modified)));
            check(delta.FolderRows.Count == 4 && delta.FolderRows.Single(f => f.RelativePath == "diff.txt").Status == FileStatus.Modified, "a FileUpdate should resolve its row in place");
            check(delta.StatusText.Contains(Lang.Get("folder_compare_progress", 1, 3, "diff.txt")), $"status bar should show compare progress: {delta.StatusText}");
            delta.ApplyFolderProgress(new(2, 3, "ghost.txt", FileUpdate: Entry("ghost.txt", FileStatus.Equal)));
            check(delta.FolderRows.Count == 5 && delta.FolderRows.Any(f => f.RelativePath == "ghost.txt"), "an update for an entry that was never announced still shows up");
            delta.ApplyFolderProgress(new(0, 0, "scan:wave", Added: [Entry("diff.txt", FileStatus.Pending, note: "内容比较中…")]));
            check(delta.FolderRows.Single(f => f.RelativePath == "diff.txt").Status == FileStatus.Modified, "a late placeholder must never overwrite an already-resolved row");
            delta.ApplyFolderProgress(new(1, 3, "diff.txt", FileUpdate: Entry("diff.txt", FileStatus.Error, note: "left: boom")));
            check(Lang.Text("left: boom") == "left: boom" && delta.FolderRows.Single(f => f.RelativePath == "diff.txt").Note == "left: boom", "error notes are kept on the entry and translated at display time");

            // ───────── 目录视图:文件夹的应答 ─────────
            // 目录模块在一个文件夹自己的列举完成时替它发出一次 FileUpdate(Total 为 0):空文件夹 → 相同,
            // 只有一侧文件的文件夹 → 不同,文件还在比较 → 不发。两侧都有的文件夹不是行,应答不能凭空造出一行;
            // 它也不是"内容比较 n/m",不能动状态栏。读不了的文件夹由一条错误条目取代,那条才是行。
            delta.ApplyFolderProgress(new(9, 0, "scan:wave:9", Added: [Entry("emptyBoth", FileStatus.Pending, directory: true, note: "子目录扫描中…"),
                Entry("mixed", FileStatus.Pending, directory: true, note: "子目录扫描中…"), Entry("vault", FileStatus.Pending, directory: true, note: "子目录扫描中…")]));
            check(delta.FolderRows.Count == 5, $"folders present on both sides are announced without becoming rows: {string.Join(", ", delta.FolderRows.Select(f => f.RelativePath))}");
            string beforeAnswers = delta.StatusText;
            foreach (var answer in new[] { Entry("emptyBoth", FileStatus.Equal, directory: true), Entry("mixed", FileStatus.Modified, directory: true), Entry("mixed", FileStatus.Pending, directory: true) })
            {
                delta.ApplyFolderProgress(new(9, 0, answer.RelativePath, FileUpdate: answer));
                check(delta.FolderRows.Count == 5, $"a folder answer ({answer.RelativePath}: {answer.Status}) must not become a row: {string.Join(", ", delta.FolderRows.Select(f => f.RelativePath))}");
            }
            check(delta.StatusText == beforeAnswers, $"a folder answer is news of the scan, not of a content compare: '{beforeAnswers}' became '{delta.StatusText}'");
            delta.ApplyFolderProgress(new(9, 0, "vault", FileUpdate: Entry("vault", FileStatus.Error, note: "right: Access to the path is denied.")));
            check(delta.FolderRows.Count == 6 && delta.FolderRows.Any(f => f.RelativePath == "vault" && f.Status == FileStatus.Error), "the error that replaces an unreadable folder is a row");
            delta.ApplyFolderProgress(new(9, 0, "vault", FileUpdate: Entry("vault", FileStatus.Equal, directory: true)));
            check(delta.FolderRows.Count == 5 && !delta.FolderRows.Any(f => f.RelativePath == "vault"), "an entry that goes back to being a folder present on both sides stops being a row");
            delta.Close();

            // ───────── 会话 / 最近比较:两个下拉框从不是空白 ─────────
            // 存量缺陷:没有会话时(以及每次选完之后)两个框整个空白,看上去像坏了;提示文字还是硬编码中文。
            // 现在第一项是标题 —— 没有条目时它写明"暂无",选过一项后框回到标题;两者都跟随界面语言。
            var picker = new MainWindow(["--plugins", pluginsPath, "--left", left, "--right", right]);
            picker.Show(); Pump(picker);
            string? Id(Control control) => Avalonia.Automation.AutomationProperties.GetAutomationId(control);
            var sessionBox = picker.GetVisualDescendants().OfType<ComboBox>().Single(c => Id(c) == "session_box_tip");
            var recentBox = picker.GetVisualDescendants().OfType<ComboBox>().Single(c => Id(c) == "recent_box_tip");
            var languageBox = picker.GetVisualDescendants().OfType<ComboBox>().Single(c => Id(c) == "md_tip_lang");
            picker.ShowSessions(new([], []));
            try
            {
                foreach (string pack in new[] { "中文", "English", "日本語" })
                {
                    languageBox.SelectedItem = pack; Pump(picker, 300);
                    check(Lang.CurrentName == pack, $"the language box should switch to {pack} (is {Lang.CurrentName})");
                    foreach (var (box, emptyKey, tipKey) in new[] { (sessionBox, "md_session_empty", "session_box_tip"), (recentBox, "md_recent_empty", "recent_box_tip") })
                    {
                        string expected = Lang.Get(emptyKey);
                        check(box.Items.Count == 1 && box.SelectedIndex == 0 && Equals(box.SelectedItem?.ToString(), expected) && expected != emptyKey && !LooksRaw(expected),
                            $"{pack}: an empty list must still name its box, in the UI language (shows '{box.SelectedItem}' of {box.Items.Count}, expected '{expected}')");
                        check(ToolTip.GetTip(box)?.ToString() == Lang.Get(tipKey), $"{pack}: the box's tip must follow the UI language (shows '{ToolTip.GetTip(box)}')");
                    }
                }
                // 有了条目:标题在前,条目随后;选一项 = 载入并比较,然后框回到标题。
                var demo = new SavedSession("demo", Path.Combine(left, "same.txt"), Path.Combine(right, "same.txt"), null, false, false, false, true, DateTime.Now, null);
                picker.ShowSessions(new([demo], [demo]));
                check(sessionBox.Items.Count == 2 && sessionBox.Items[0]?.ToString() == Lang.Get("sessions") && sessionBox.Items[1]?.ToString() == "📌 demo · same.txt ↔ same.txt" && sessionBox.SelectedIndex == 0,
                    $"named sessions should follow their caption: {string.Join(" | ", sessionBox.Items)}");
                check(recentBox.Items.Count == 2 && recentBox.Items[0]?.ToString() == Lang.Get("recent") && recentBox.Items[1]?.ToString() == "🕘 same.txt ↔ same.txt" && recentBox.SelectedIndex == 0,
                    $"recent comparisons should follow their caption: {string.Join(" | ", recentBox.Items)}");
                recentBox.SelectedIndex = 1;
                var loaded = DateTime.UtcNow.AddSeconds(20);
                while (recentBox.SelectedIndex != 0 && DateTime.UtcNow < loaded) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
                check(picker.PathBarTexts == (demo.Left, demo.Right), $"picking a recent comparison must load its paths (shows {picker.PathBarTexts.Left} ↔ {picker.PathBarTexts.Right})");
                check(recentBox.SelectedIndex == 0 && Equals(recentBox.SelectedItem?.ToString(), Lang.Get("recent")), "after a choice the box returns to its caption instead of going blank");
            }
            finally
            {
                Lang.Apply("中文");
                if (!Equals(languageBox.SelectedItem, "中文")) { languageBox.SelectedItem = "中文"; Pump(picker, 300); }
                picker.Close();
            }

            // ───────── 目录视图:名称筛选 / 状态筛选 ─────────
            string filterRoot = Path.Combine(root, "filter");
            foreach (var name in new[] { "src/a.cs", "src/b.txt", "readme.md", Path.Combine("docs", "deep", "c.cs"), Path.Combine("x", "y", "z.md") })
            {   Put(Path.Combine(filterRoot, "L"), name, "L\n"); Put(Path.Combine(filterRoot, "R"), name, name.EndsWith(".md") ? "R\n" : "L\n"); }
            var filtered = OpenFolder(pluginsPath, Path.Combine(filterRoot, "L"), Path.Combine(filterRoot, "R"), check, "filter");
            int total = filtered.FolderRows.Count;
            check(total == 5 && filtered.FolderTotalFiles == 5, $"5 files expected, got rows={total} total={filtered.FolderTotalFiles}");

            filtered.SetFolderFilter("*.cs", regexMode: false, statusFilter: 0);
            check(filtered.FolderRows.Select(f => f.RelativePath).Order().SequenceEqual(["docs/deep/c.cs".Replace('/', Path.DirectorySeparatorChar), "src/a.cs".Replace('/', Path.DirectorySeparatorChar)]),
                $"glob '*.cs' should match both .cs files at any depth: {string.Join(", ", filtered.FolderRows.Select(f => f.RelativePath))}");
            check(filtered.FolderFilterError is null && filtered.FolderVisibleCount == 2 && filtered.FolderVisibleCount <= filtered.FolderTotalFiles, "filter counts");
            filtered.SetFolderFilter("src", regexMode: false, statusFilter: 0);
            check(filtered.FolderRows.Count == 2, $"substring should match the relative path: {string.Join(", ", filtered.FolderRows.Select(f => f.RelativePath))}");
            filtered.SetFolderFilter("A.CS", regexMode: false, statusFilter: 0);
            check(filtered.FolderRows.Count == 1, "name filter is case-insensitive");
            filtered.SetFolderFilter(@"\.txt$|^docs", regexMode: true, statusFilter: 0);
            check(filtered.FolderRows.Count == 2, $"regex mode: {string.Join(", ", filtered.FolderRows.Select(f => f.RelativePath))}");
            filtered.SetFolderFilter("[", regexMode: true, statusFilter: 0);
            check(filtered.FolderRows.Count == 0 && !string.IsNullOrEmpty(filtered.FolderFilterError), "an invalid regex must show an error instead of throwing or matching everything");
            filtered.SetFolderFilter("", regexMode: false, statusFilter: 0);
            check(filtered.FolderRows.Count == 5 && filtered.FolderFilterError is null, "clearing the filter restores every row and the error");
            filtered.SetFolderFilter("", regexMode: false, statusFilter: 1);   // 有差异:readme.md 与 x/y/z.md
            check(filtered.FolderRows.Count == 2 && filtered.FolderRows.All(f => f.Status != FileStatus.Equal), "status filter 'differences' should keep only non-identical files");
            filtered.SetFolderFilter("", regexMode: false, statusFilter: 2);   // 相同:两个 .cs 与 src/b.txt
            check(filtered.FolderRows.Count == 3 && filtered.FolderRows.All(f => f.Status == FileStatus.Equal), "status filter 'identical' should keep only identical files");
            filtered.SetFolderFilter("*.cs", regexMode: false, statusFilter: 2);   // 名称与状态筛选叠加
            check(filtered.FolderRows.Count == 2, "name and status filters should combine");
            filtered.SetFolderFilter("", regexMode: false, statusFilter: 1);
            filtered.SetFolderFilter("", regexMode: false, statusFilter: 0);

            // 行文本:状态用界面语言,不再输出枚举名;带上大小与修改时间。
            var row = filtered.FolderRows.First(f => f.RelativePath.EndsWith("a.cs", StringComparison.Ordinal));
            string zh = filtered.FolderRowText(row, left: true);
            check(zh.Contains(Lang.Get("file_status_equal")) && !LooksRaw(zh) && zh.Contains("a.cs") && zh.Contains("B"), $"row text should be localized and carry size: {zh}");
            Lang.Apply("English");
            string en = filtered.FolderRowText(row, left: true);
            check(en.Contains(Lang.Get("file_status_equal")) && !en.Contains("相同"), $"row text should follow the UI language: {en}");
            Lang.Apply("中文");
            var oneSide = filtered.FolderRows.First(f => f.RelativePath == Path.Combine("src", "b.txt") || f.Status == FileStatus.Modified);
            check(filtered.FolderRowText(oneSide, left: false).Length > 0, "right column text");

            // ───────── 跨文件打开:左 A ↔ 右 B ─────────
            string crossRoot = Path.Combine(root, "cross");
            Put(Path.Combine(crossRoot, "L"), "old-name.txt", "same content\n"); Put(Path.Combine(crossRoot, "R"), "new-name.txt", "same content\n");
            var cross = OpenFolder(pluginsPath, Path.Combine(crossRoot, "L"), Path.Combine(crossRoot, "R"), check, "cross");
            var oldRow = cross.FolderRows.Single(f => f.RelativePath == "old-name.txt");
            var newRow = cross.FolderRows.Single(f => f.RelativePath == "new-name.txt");
            OnUi(cross, cross.OpenPickedPair());
            check(cross.SessionLeft is null && cross.StatusText.Length > 0, "opening a pair before both sides are picked must explain instead of opening");
            cross.PickFolderSide(left: true, oldRow);
            OnUi(cross, cross.OpenPickedPair());
            check(cross.SessionLeft is null, "a half-picked pair must not open");
            cross.PickFolderSide(left: false, newRow);
            OnUi(cross, cross.OpenPickedPair()); Pump(cross);
            check(cross.SessionLeft == Path.Combine(crossRoot, "L", "old-name.txt") && cross.SessionRight == Path.Combine(crossRoot, "R", "new-name.txt"),
                $"cross-file open should load left A against right B: {cross.SessionLeft} ↔ {cross.SessionRight}");
            check(cross.FolderRows.Count == 2, "the folder list must survive opening a file pair from it");
            cross.Close();

            var deadline = DateTime.MinValue;   // 路径栏用例的等待截止时间(在下面赋值)
            // ───────── 视觉验收发现的两类缺陷 ─────────
            // (1) 单侧条目的状态曾显示成原始键名 file_status_leftonly / file_status_rightonly:
            //     状态文本是用 "file_status_" + 枚举名小写 拼出来的,而 LeftOnly 对应的键是 file_status_left_only。
            foreach (string pack in new[] { "中文", "English", "日本語", "한국어", "繁體中文" })
            {
                Lang.Apply(pack);
                foreach (FileStatus status in Enum.GetValues<FileStatus>())
                {
                    string text = MainWindow.FolderStatusText(status);
                    string key = "file_status_" + status.ToString().ToLowerInvariant();
                    string expected = status is FileStatus.LeftOnly or FileStatus.RightOnly
                        ? Lang.Get("file_status_" + (status == FileStatus.LeftOnly ? "left_only" : "right_only")) : Lang.Get(key);
                    check(text == expected && !Regex.IsMatch(text, "^[a-z_]+$"), $"{pack}: status {status} should read '{expected}', got '{text}'");
                }
                // 真实行文本:左侧独有 / 右侧独有的文件与目录,四种类别都要是本地化文字。
                foreach (var entry in new[]
                {
                    Entry("a.txt", FileStatus.LeftOnly), Entry("dirA", FileStatus.LeftOnly, directory: true),
                    Entry("b.txt", FileStatus.RightOnly), Entry("dirB", FileStatus.RightOnly, directory: true),
                })
                {
                    string leftText = filtered.FolderRowText(entry, left: true), rightText = filtered.FolderRowText(entry, left: false);
                    string wanted = Lang.Get(entry.Status == FileStatus.LeftOnly ? "file_status_left_only" : "file_status_right_only");
                    check((entry.Status == FileStatus.LeftOnly ? leftText : rightText).Contains(wanted) && !Regex.IsMatch(leftText + rightText, "file_status_[a-z_]+"),
                        $"{pack}: a one-side row should read '{wanted}': {leftText} | {rightText}");
                }
            }
            Lang.Apply("中文");

            // (2) 切换语言后页签标题曾变长却被截断("Aligned ["、"Folder C("):页签条没有重新测量。
            //     用仍然打开着的 filtered 窗口,走真实的语言下拉框。
            var tabs = filtered.GetVisualDescendants().OfType<TabControl>().Single();
            var summaryBox = filtered.GetVisualDescendants().OfType<ComboBox>().Single(c => Avalonia.Automation.AutomationProperties.GetAutomationId(c) == "md_tip_lang");
            int selectedIndexBefore = tabs.SelectedIndex;
            summaryBox.SelectedItem = "English"; Pump(filtered, 400);
            var pages = tabs.Items.OfType<TabItem>().ToArray();
            check(pages.Length == 3 && pages.All(t => t.IsAttachedToVisualTree()), "every tab must stay attached to the window after the language switch");
            check(tabs.SelectedIndex == selectedIndexBefore && ReferenceEquals(tabs.SelectedContent, pages[selectedIndexBefore].Content), "the selected tab and its content survive the language switch");
            foreach (TabItem tab in pages)
            {
                var header = tab.GetVisualDescendants().OfType<TextBlock>().First();
                check(tab.Bounds.Width + 0.5 >= header.Bounds.Width && header.Bounds.Width + 0.5 >= header.DesiredSize.Width,
                    $"tab header '{tab.Header}' is clipped after the language switch: tab {tab.Bounds.Width:0}px, header box {header.Bounds.Width:0}px, header needs {header.DesiredSize.Width:0}px");
            }
            check(string.Equals(pages[2].Header as string, Lang.Get("md_tab_folder"), StringComparison.Ordinal), "tab headers follow the language switch");

            // 用一个状态栏仍停在"目录汇总"的窗口:切语言后汇总应按新语言重新生成,而不是被提示语顶掉。
            check(filtered.StatusText == Lang.Get("status_summary", 5, 3, 2, 0), $"precondition: the status bar shows the folder summary: {filtered.StatusText}");
            summaryBox.SelectedItem = "English"; Pump(filtered, 300);
            check(filtered.StatusText == Lang.Get("status_summary", 5, 3, 2, 0), $"the status bar should re-render the folder summary in the new language: {filtered.StatusText}");
            summaryBox.SelectedItem = "中文"; Pump(filtered, 300);
            check(filtered.StatusText == Lang.Get("status_summary", 5, 3, 2, 0), $"back to Chinese: {filtered.StatusText}");

            // ───────── 路径栏:回车即比较 ─────────
            var bars = new MainWindow(["--plugins", pluginsPath]);
            bars.Show(); Pump(bars);
            string LeftOnlyMessage(string path) => Lang.Text("左侧路径不存在：" + path);
            bars.SetPathBars("   ", root);
            check(!bars.CommitPathBars() && bars.StatusText == Lang.Text("请填写左右两侧路径。"), $"blank paths should be refused with a message: {bars.StatusText}");
            bars.SetPathBars(Path.Combine(root, "nope"), root);
            string missing = Path.GetFullPath(Path.Combine(root, "nope"));
            check(!bars.CommitPathBars() && bars.StatusText == LeftOnlyMessage(missing), $"a missing left path should be refused: {bars.StatusText}");
            bars.SetPathBars(root, Path.Combine(root, "nope"));
            check(!bars.CommitPathBars() && bars.StatusText == Lang.Text("右侧路径不存在：" + missing), $"a missing right path should be refused: {bars.StatusText}");
            bars.SetFolderFilter("", regexMode: false, statusFilter: 0);
            bars.SetPathBars(Path.Combine(left, "diff.txt"), root);
            check(!bars.CommitPathBars() && bars.StatusText == Lang.Text("左右须同为文件或同为目录。"), $"a file against a folder should be refused: {bars.StatusText}");
            bars.SetPathBars("bad\0path", root);
            check(!bars.CommitPathBars() && bars.StatusText.StartsWith("路径不合法", StringComparison.Ordinal), $"an unparseable path should be refused: {bars.StatusText}");
            check(!bars.FolderCompareComplete && bars.FolderRows.Count == 0 && bars.PathBarBoxes[0].Text == "bad\0path", "a refused commit must not start a compare or rewrite the boxes");

            // 成功:两侧都是目录 → 规范成绝对路径(去空白)并开始比较。
            bars.SetPathBars("   " + left + "   ", "  " + right + "  ");
            check(bars.CommitPathBars(), $"a valid folder pair should commit: {bars.StatusText}");
            deadline = DateTime.UtcNow.AddSeconds(15);
            while (!bars.FolderCompareComplete && DateTime.UtcNow < deadline) Pump(bars, 20);
            check(bars.FolderCompareComplete, $"committing the path bars should run the compare (status: {bars.StatusText})");
            check(bars.PathBarTexts.Left == left && bars.PathBarTexts.Right == right, $"paths should be normalised to absolute paths: {bars.PathBarTexts.Left} | {bars.PathBarTexts.Right}");
            check(bars.FolderTotalFiles == 5 && bars.FolderRows.Count == 7, $"L vs R: 5 files plus the two one-side folders as rows (files={bars.FolderTotalFiles}, rows={bars.FolderRows.Count})");
            check(bars.FolderRows.Count(f => f.IsDirectory) == 2 && bars.FolderRows.Where(f => f.IsDirectory).All(f => f.RelativePath is "onlyL" or "onlyR"),
                "folders that exist on one side only are listed; folders present on both sides are structure");

            // 真实的 Enter 按键事件走同一条路;比较进行中再次回车不应重入。
            // 真实的 Enter 按键走同一条路。判据不依赖时序:换一对"结果形态不同"的目录
            // (L vs R 是 7 行:5 个文件 + 2 个单侧目录;筛选树是 5 行),看列表有没有变成新结果。
            string filterLeft = Path.Combine(filterRoot, "L"), filterRight = Path.Combine(filterRoot, "R");
            bars.SetPathBars(filterLeft, filterRight);
            bars.PathBarBoxes[1].Focus();
            bars.KeyPressQwerty(Avalonia.Input.PhysicalKey.A, Avalonia.Input.RawInputModifiers.None);   // 普通按键
            Pump(bars);
            check(bars.FolderRows.Count == 7, "a non-Enter key must not start a compare");
            bars.KeyPressQwerty(Avalonia.Input.PhysicalKey.Enter, Avalonia.Input.RawInputModifiers.None);
            check(bars.PathBarTexts.Left == filterLeft && bars.PathBarTexts.Right == filterRight, "Enter should leave the resolved paths in the boxes");
            var enterDeadline = DateTime.UtcNow.AddSeconds(15);
            while (bars.FolderRows.Count != 5 && DateTime.UtcNow < enterDeadline) Pump(bars, 20);
            check(bars.FolderCompareComplete && bars.FolderRows.Count == 5 && bars.FolderTotalFiles == 5,
                $"Enter on a path box should run the compare for the typed pair (rows={bars.FolderRows.Count}, complete={bars.FolderCompareComplete}, status={bars.StatusText})");
            // 路径框里是相对写法(以当前工作目录为基准):提交后被规范成绝对路径 —— 之后"同路径重对齐"等判断才有稳定依据。
            Environment.CurrentDirectory = root;
            try
            {
                // macOS 的临时目录在符号链接之后(/var → /private/var):以当前工作目录为基准解析相对路径得到的
                // 是真实路径,所以期望值必须用同一种方式算,不能拿测试手里的 /var/… 字符串硬比。
                string expectedLeft = Path.GetFullPath("L"), expectedRight = Path.GetFullPath("R");
                check(!expectedLeft.EndsWith(Path.DirectorySeparatorChar) && Directory.Exists(expectedLeft), "test precondition: relative base resolves to the tree");
                bars.SetPathBars("  " + Path.GetRelativePath(root, left) + Path.DirectorySeparatorChar + "  ", Path.GetRelativePath(root, right));
                check(bars.CommitPathBars(), $"a relative folder pair should commit: {bars.StatusText}");
                // 左侧故意带尾部分隔符:提交后不得残留(否则 "L/" 与 "L" 会被当成两个路径)。
                check(bars.PathBarTexts.Left == expectedLeft && bars.PathBarTexts.Right == expectedRight,
                    $"relative input should resolve to absolute paths without a trailing separator: {bars.PathBarTexts.Left} | {bars.PathBarTexts.Right}");
            }
            finally { Environment.CurrentDirectory = originalDirectory; }
            enterDeadline = DateTime.UtcNow.AddSeconds(15);
            while (bars.FolderRows.Count != 7 && DateTime.UtcNow < enterDeadline) Pump(bars, 20);
            check(bars.FolderRows.Count == 7 && bars.FolderCompareComplete, "compare after a relative-path commit");
            deadline = DateTime.UtcNow.AddSeconds(15);
            while (bars.FolderCompareComplete is false && DateTime.UtcNow < deadline) Pump(bars, 20);
            Pump(bars);
            bars.Close();
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Lang.Apply("中文");
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
