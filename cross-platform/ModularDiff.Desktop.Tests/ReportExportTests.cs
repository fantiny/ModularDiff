using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ModularDiff.Contracts;
using ModularDiff.Core;
using ModularDiff.Desktop;

/// <summary>macOS 的报告导出 —— WPF 的 ExportButton + FormatBox 在这里缺席了很久。
/// 格式全部来自 IResultFormatter 插件(文本 / JSON / HTML)。导出的是"当前比较":目录页是整个目录比较(扫描结束之后),
/// 差异 / 编辑页是当前这一对文件。写盘是原子的,并且绝不写进被比较的文件或目录。</summary>
internal static class ReportExportTests
{
    private static void Pump(Window window, int milliseconds = 120)
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

    private static MainWindow Open(string[] args, Action<bool, string> check, Func<MainWindow, bool> ready, string what)
    {
        var window = new MainWindow(args);
        window.Show();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!ready(window) && DateTime.UtcNow < deadline) Pump(window, 20);
        check(ready(window), $"{what}: the window did not get ready (status: {window.StatusText})");
        return window;
    }

    /// <summary>写到一半就失败的格式化器:报告不得留下半截文件。</summary>
    private sealed class FailingFormatter : IResultFormatter
    {
        public string Id => "test-fails-halfway";
        public string DisplayName => "Test formatter that fails halfway";
        public string FileExtension => ".txt";
        public Task WriteAsync(ComparisonResult result, TextWriter writer, CancellationToken cancellationToken = default)
        {
            writer.Write("the first half of a report");
            return Task.FromException(new IOException("disk full (test)"));
        }
    }

    public static void Run(string pluginsPath, PluginCatalog plugins, Action<bool, string> check)
    {
        check(Dispatcher.UIThread.CheckAccess() && SynchronizationContext.Current is not null, "report export tests create windows: they need the UI thread and its synchronization context");
        string root = Path.Combine(Path.GetTempPath(), "ModularDiff-export-" + Guid.NewGuid().ToString("N"));
        string left = Path.Combine(root, "L"), right = Path.Combine(root, "R"), outDir = Path.Combine(root, "out");
        void Put(string side, string relative, byte[] bytes) { string path = Path.Combine(side, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); }
        static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);
        Put(left, "same.txt", Bytes("a\n")); Put(right, "same.txt", Bytes("a\n"));
        Put(left, "diff.txt", Bytes("one\ntwo\n")); Put(right, "diff.txt", Bytes("one\nTWO\n"));
        Put(left, "onlyL.txt", Bytes("x\n")); Put(right, "onlyR.txt", Bytes("y\n"));
        Put(left, Path.Combine("sub", "nested.txt"), Bytes("1\n")); Put(right, Path.Combine("sub", "nested.txt"), Bytes("2\n"));
        Put(left, "bin.dat", [0x00, 0xFF, 0x00, 0x01]); Put(right, "bin.dat", [0x00, 0xFF, 0x00, 0x02]);   // 无效 UTF-8:走二进制路径
        Directory.CreateDirectory(outDir);
        IResultFormatter Formatter(string id) => plugins.Get<IResultFormatter>(id);
        IResultFormatter[] all = plugins.GetAll<IResultFormatter>().ToArray();
        string Text(string path) => File.ReadAllText(path);
        try
        {
            // ───────── 工具栏:格式下拉 + 导出按钮 ─────────
            var empty = Open(["--plugins", pluginsPath, "--left", left, "--right", right], check, _ => true, "fresh window");
            var formatBox = ById<ComboBox>(empty, "format_report");
            var exportButton = ById<Button>(empty, "export");
            check(all.Select(f => f.Id).Order().SequenceEqual(["html", "json", "text"]), $"the test needs the text, json and html formatters, found {string.Join(", ", all.Select(f => f.Id))}");
            check(formatBox.Items.Count == all.Length, $"the format box lists every report formatter plugin ({all.Length}), it lists {formatBox.Items.Count}");
            check(formatBox.Items.Cast<string>().SequenceEqual(all.Select(f => Lang.Text(f.DisplayName))),
                $"format names come from the plugins and follow the UI language: {string.Join(" | ", formatBox.Items.Cast<string>())}");
            check(ToolTip.GetTip(exportButton)?.ToString() == Lang.Get("export") && ToolTip.GetTip(formatBox)?.ToString() == Lang.Get("format_report"),
                $"the export button and the format box carry their tips in the UI language (shows '{ToolTip.GetTip(exportButton)}' / '{ToolTip.GetTip(formatBox)}')");
            check(empty.SelectedReportFormatter?.Id == "html", $"HTML is the default format, as the report people share (is '{empty.SelectedReportFormatter?.Id}')");

            // ───────── 还没有比较:说明情况,不写文件 ─────────
            string early = Path.Combine(outDir, "early.txt");
            check(!OnUi(empty, empty.ExportReportAsync(early)) && !File.Exists(early) && empty.StatusText == Lang.Get("md_export_nothing"),
                $"with nothing compared the export must say so and write nothing (status '{empty.StatusText}')");
            empty.Close();

            // ───────── 目录还在扫描:同样不导出;扫描结束后才行 ─────────
            var scanning = Open(["--plugins", pluginsPath, "--left", left, "--right", right], check, _ => true, "window before its compare");
            string tooEarly = Path.Combine(outDir, "too-early.txt");
            check(scanning.CommitPathBars(), "Enter on the path bars starts the compare");
            // 决定"导出什么"发生在 ExportReportAsync 的同步开头,此时扫描定时器刚启动、比较的后续体还没机会跑。
            // 状态栏也必须在那一刻读:一旦让出线程,小目录的比较就结束并把汇总写进状态栏(这条断言曾靠时序碰巧通过)。
            var tooEarlyExport = scanning.ExportReportAsync(tooEarly);
            string answered = scanning.StatusText;
            check(!OnUi(scanning, tooEarlyExport) && !File.Exists(tooEarly) && answered == Lang.Get("md_export_scanning"),
                $"while the folder scan runs the export must wait for it and write nothing (answered '{answered}')");
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!scanning.FolderCompareComplete && DateTime.UtcNow < deadline) Pump(scanning, 20);
            check(scanning.FolderCompareComplete, $"the folder compare did not finish (status: {scanning.StatusText})");
            check(OnUi(scanning, scanning.ExportReportAsync(tooEarly, Formatter("text"))) && File.Exists(tooEarly), "once the folder compare has finished its report can be exported");
            scanning.Close();

            // ───────── 扫描中只到了根目录那一波:它还不是一份报告 ─────────
            // 根波快照只有顶层的内容,导出来却像一份完整的报告。
            var partial = new MainWindow(["--plugins", pluginsPath, "--left", left, "--right", right]);
            partial.Show(); Pump(partial);
            partial.ApplyFolderProgress(new(0, 0, "scan:root", PartialResult: new ComparisonResult(left, right, "directories", "myers",
                [new FileComparison("diff.txt", FileStatus.Pending, [], Note: "内容比较中…")], [])));
            string premature = Path.Combine(outDir, "premature.txt");
            check(!OnUi(partial, partial.ExportReportAsync(premature, Formatter("text"))) && !File.Exists(premature),
                $"the root wave of a running folder scan must not be exported as a report (status '{partial.StatusText}')");
            partial.Close();

            // ───────── 目录比较:三种格式 ─────────
            var folder = Open(["--plugins", pluginsPath, "--left", left, "--right", right, "--auto-compare"], check, w => w.FolderCompareComplete, "folder compare");
            string html = Path.Combine(outDir, "folder.html");
            check(OnUi(folder, folder.ExportReportAsync(html)) && Text(html).Contains("<!DOCTYPE html>") && Text(html).Contains("ModularDiff 比较报告")
                && Text(html).Contains("status-Modified\">Modified</td><td>diff.txt") && Text(html).Contains("status-RightOnly\">RightOnly</td><td>onlyR.txt"),
                "the default format writes an HTML report of the folder comparison");
            check(folder.StatusText == Lang.Get("status_export_saved", Path.GetFullPath(html)), $"the status bar names the saved report (shows '{folder.StatusText}')");
            string json = Path.Combine(outDir, "folder.json");
            check(OnUi(folder, folder.ExportReportAsync(json, Formatter("json"))), "the json formatter exports");
            using (var document = JsonDocument.Parse(Text(json)))
            {
                var entries = document.RootElement.GetProperty("Files").EnumerateArray().ToArray();
                check(entries.Length == 7 && entries.Count(e => !e.GetProperty("IsDirectory").GetBoolean()) == 6
                    && entries.Single(e => e.GetProperty("RelativePath").GetString() == "diff.txt").GetProperty("Status").GetString() == "Modified",
                    $"the JSON report is the whole comparison result: 6 files and the one folder entry, statuses included (got {entries.Length} entries)");
            }
            string text = Path.Combine(outDir, "folder.txt");
            check(OnUi(folder, folder.ExportReportAsync(text, Formatter("text"))) && Text(text).Contains("[Modified] diff.txt") && Text(text).Contains("[RightOnly] onlyR.txt")
                && Text(text).Contains("Files: 6; changed: 5; errors: 0"), "the text formatter exports the same facts in text");

            // ───────── 绝不写进被比较的文件或目录 ─────────
            string inside = Path.Combine(left, "report.html");
            check(!OnUi(folder, folder.ExportReportAsync(inside)) && !File.Exists(inside)
                && folder.StatusText == Lang.Get("status_export_failed", Lang.Get("err_export_outside")),
                $"a report inside a compared folder would show up as a one-sided file in the next compare: refused (status '{folder.StatusText}')");
            string input = Path.Combine(right, "diff.txt"), before = Text(input);
            check(!OnUi(folder, folder.ExportReportAsync(input)) && Text(input) == before, "a report must never overwrite one of the compared files");

            // ───────── 失败不留半截文件;已有报告被整体替换 ─────────
            string failed = Path.Combine(outDir, "failed.txt");
            check(!OnUi(folder, folder.ExportReportAsync(failed, new FailingFormatter())) && !File.Exists(failed)
                && !Directory.EnumerateFiles(outDir, "failed.txt.tmp-*").Any() && folder.StatusText == Lang.Get("status_export_failed", "disk full (test)"),
                $"a formatter that fails halfway leaves no file and no temp file behind (status '{folder.StatusText}')");
            File.WriteAllText(text, "an old report\n");
            check(OnUi(folder, folder.ExportReportAsync(text, Formatter("text"))) && Text(text).StartsWith("Left:", StringComparison.Ordinal) && !Text(text).Contains("an old report"),
                "exporting over an old report replaces it whole");

            // ───────── 换语言:格式名跟着换,所选格式不变 ─────────
            int htmlIndex = Array.FindIndex(all, f => f.Id == "html");
            formatBox = ById<ComboBox>(folder, "format_report");
            formatBox.SelectedIndex = Array.FindIndex(all, f => f.Id == "json"); Pump(folder, 200);
            check(folder.SelectedReportFormatter?.Id == "json", "picking a format selects it");
            var languageBox = ById<ComboBox>(folder, "md_tip_lang");
            try
            {
                foreach (string pack in new[] { "English", "日本語" })
                {
                    languageBox.SelectedItem = pack; Pump(folder, 400);
                    formatBox = ById<ComboBox>(folder, "format_report");
                    check(formatBox.Items.Cast<string>().SequenceEqual(all.Select(f => Lang.Text(f.DisplayName))), $"{pack}: the format names follow the language (shows {string.Join(" | ", formatBox.Items.Cast<string>())})");
                    check(folder.SelectedReportFormatter?.Id == "json", $"{pack}: a language switch must keep the chosen format (is '{folder.SelectedReportFormatter?.Id}')");
                    check(ToolTip.GetTip(ById<Button>(folder, "export"))?.ToString() == Lang.Get("export"), $"{pack}: the export tip follows the language");
                }
            }
            // 收尾要先拨下拉框:处理函数只在语言确实不同时才应用并保存。先 Apply 再拨框,已保存的语言就停在最后一次
            // 切换的那种,之后同一进程里新建的每个窗口都会以它启动(每个窗口构造时应用的是已保存的偏好)。
            finally { if (!Equals(languageBox.SelectedItem, "中文")) { languageBox.SelectedItem = "中文"; Pump(folder, 400); } Lang.Apply("中文"); }
            formatBox.SelectedIndex = htmlIndex; Pump(folder, 100);

            // ───────── 文本会话还开着,但用户已经回到目录页:导出的是目录比较 ─────────
            var diffRow = folder.FolderRows.Single(f => f.RelativePath == "diff.txt");
            folder.PickFolderSide(true, diffRow); folder.PickFolderSide(false, diffRow);
            OnUi(folder, folder.OpenPickedPair()); Pump(folder);
            var tabs = folder.GetVisualDescendants().OfType<TabControl>().Single();
            check(tabs.SelectedIndex == 0, "opening a pair from the list shows the diff tab");
            string backOnFolder = Path.Combine(outDir, "folder-again.txt");
            tabs.SelectedIndex = 2; Pump(folder, 100);
            check(OnUi(folder, folder.ExportReportAsync(backOnFolder, Formatter("text"))) && Text(backOnFolder).Contains("[RightOnly] onlyR.txt") && !Text(backOnFolder).Contains("+      2 | TWO"),
                "back on the folder tab the report is the folder comparison, not the pair that is still loaded behind it");
            folder.Close();

            // ───────── 文件对:报告描述磁盘上的文件;未保存的修改不在其中,并如实说明 ─────────
            string pairLeft = Path.Combine(left, "diff.txt"), pairRight = Path.Combine(right, "diff.txt");
            var pair = Open(["--plugins", pluginsPath, "--left", pairLeft, "--right", pairRight, "--auto-compare"], check,
                w => w.GetVisualDescendants().OfType<AlignedRowsView>().Any(v => v.RowCount > 0), "file compare");
            string pairReport = Path.Combine(outDir, "pair.txt");
            check(OnUi(pair, pair.ExportReportAsync(pairReport, Formatter("text"))) && Text(pairReport).Contains("[Modified] diff.txt")
                && Text(pairReport).Contains("-      2 | two") && Text(pairReport).Contains("+      2 | TWO") && Text(pairReport).Contains("Files: 1; changed: 1"),
                "the report of a file pair is that pair, not the folder it was opened from");
            // 比较之后磁盘上的文件变了:报告是导出时对这两个文件的比较。
            File.AppendAllText(pairRight, "three\n");
            OnUi(pair, pair.ExportReportAsync(pairReport, Formatter("text")));
            check(Text(pairReport).Contains("+      3 | three"), "the report describes the files as they are on disk when it is written");
            pair.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); pair.UpdateLayout();
            pair.BeginEditRow(0, true);
            var cellEditor = pair.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "CellEditor" && t.IsVisible);
            check(cellEditor is not null, "inline cell editor realized");
            cellEditor!.Text += " edited";
            Dispatcher.UIThread.RunJobs();
            OnUi(pair, pair.ExportReportAsync(pairReport, Formatter("text")));
            check(pair.StatusText == Lang.Get("status_export_saved", Path.GetFullPath(pairReport)) + " · " + Lang.Get("md_export_unsaved")
                && !Text(pairReport).Contains("edited"), $"unsaved edits are not on disk, so not in the report — and the status bar says so (status '{pair.StatusText}')");
            pair.Close();

            // ───────── 二进制对(十六进制视图):导出的就是这一对 ─────────
            var binary = Open(["--plugins", pluginsPath, "--left", Path.Combine(left, "bin.dat"), "--right", Path.Combine(right, "bin.dat"), "--auto-compare"], check,
                w => w.HexViewActive, "binary compare");
            string binaryReport = Path.Combine(outDir, "binary.txt");
            check(OnUi(binary, binary.ExportReportAsync(binaryReport, Formatter("text"))) && Text(binaryReport).Contains("bin.dat (byte comparison)") && Text(binaryReport).Contains("Files: 1;"),
                $"from the hex view the report is that binary pair (report: {Text(binaryReport).Replace("\n", " | ")})");
            binary.Close();
        }
        finally
        {
            Lang.Apply("中文");
            try { Directory.Delete(root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.WriteLine("export test files await process exit: " + root); }
        }
    }
}
