using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Avalonia.Headless;
using Avalonia.Controls;
using ModularDiff.Contracts;
using ModularDiff.Core;
using ModularDiff.Desktop;

AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
string pluginsPath = Path.GetFullPath(args[0]);
using var plugins = PluginCatalog.Load(pluginsPath);
void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
Check(plugins.Diagnostics.Count == 0, "plugin load");
ModularDiff.Desktop.Lang.Apply("中文");   // 测试基线语言(机器上可能残留其他选择)
ModularDiff.Desktop.Palette.SaveLanguage("中文");
string uiRoot = Path.Combine(Path.GetTempPath(), "ModularDiff-ui-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(uiRoot);
File.WriteAllText(Path.Combine(uiRoot, "left.cs"), "// Comparison sample\nclass Calculator\n{\n    int Sum(int a, int b) => a + b;\n}\n--bg:#0b0d10;--panel:#11151a;\n--line:#2a313b;--code:#0b0b11;\n--gap:8px;--radius:12px;\n");
File.WriteAllText(Path.Combine(uiRoot, "right.cs"), "// Comparison sample\nclass Calculator\n{\n    // Handle subtraction\n    int Sum(int a, int b) => a - b;\n}\n--bg:#0b0d10;--panel:#11151a;\n--line:#2a313b;--code:#0b0b11;\n--gap:8px;--radius:12px;\n");
var window = new MainWindow(["--plugins", pluginsPath, "--left", Path.Combine(uiRoot, "left.cs"), "--right", Path.Combine(uiRoot, "right.cs"), "--auto-compare"]);
window.Show();
Check(window.Content is Grid && window.IsVisible, "Avalonia window initializes on headless platform");
var deadline = DateTime.UtcNow.AddSeconds(15);
while (!window.GetVisualDescendants().OfType<AlignedRowsView>().Any(v => v.RowCount > 0) && DateTime.UtcNow < deadline)
{ Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Thread.Sleep(10); }
Console.WriteLine(string.Join(" | ", ((Grid)window.Content!).Children.OfType<TextBlock>().Select(t => t.Text)));
Check(window.GetVisualDescendants().OfType<AlignedRowsView>().Any(v => v.RowCount > 0), "GUI compare populates aligned rows");
window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
using (var frame = window.CaptureRenderedFrame())
{
    Check(frame is not null, "GUI renders a frame");
    if (args.Length > 1) frame!.Save(args[1]);
}
// 差异对齐页就地编辑:改单元格 → 会话变脏 → ▶ 内存重对齐(不弹保存对话)。
var rowsView = window.GetVisualDescendants().OfType<AlignedRowsView>().Single();
window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout();
window.BeginEditRow(0, true);
var cellEditor = window.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "CellEditor" && t.IsVisible);
Check(cellEditor is not null, "inline cell editor realized");
var cssMenuRow = rowsView.Rows.First(r => r.Left?.Text.StartsWith("--bg:") == true);
Check(window.CreateSplitMenu(cssMenuRow, false) is { ItemCount: > 0 }, "css line offers split context menu");
cellEditor!.Text += " // edited";
Dispatcher.UIThread.RunJobs();
Check(((Grid)window.Content!).Children.OfType<TextBlock>().Any(t => t.Text?.Contains("未保存") == true), "inline cell edit marks session dirty");
var compareButton = ((Grid)window.Content!).Children.OfType<StackPanel>().Single(p => p.Orientation == Avalonia.Layout.Orientation.Horizontal && p.Children.OfType<Button>().Any()).Children.OfType<Button>().First();
compareButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
var realign = DateTime.UtcNow.AddSeconds(5);
while (DateTime.UtcNow < realign) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Thread.Sleep(10); }
Check(rowsView.RowCount > 0 && ((Grid)window.Content!).Children.OfType<TextBlock>().Any(t => t.Text?.Contains("未保存") == true), "refresh realigns inline edits without save prompt");
// 跨行文本选区:像文本框一样切片复制。
int classIndex = Enumerable.Range(0, rowsView.RowCount).First(i => rowsView.Rows[i].Left?.Text.StartsWith("class") == true);
rowsView.SetTextSelection(0, 0, classIndex, 3, true);
var sliced = rowsView.SelectedText();
Check(sliced.StartsWith("// Comparison sample") && sliced.EndsWith("\ncla"), $"cross-row text selection slice (got [{sliced}])");
rowsView.ClearTextSelection();
Check(rowsView.SelectedText().Length == 0, "text selection clears");
// 多选 + 批量换行 + ⟳ 重对齐:两条 css 行各拆成两行,行数 +2。
var cssRows = rowsView.Rows.Where(r => r.Left?.Text.StartsWith("--") == true && !r.Left!.Text.StartsWith("--gap:")).ToList();
Check(cssRows.Count == 2, "two css rows present for batch split");
rowsView.SelectRows(cssRows);
var splitItem = window.CreateSplitMenu(cssRows[0], false)!.Items.OfType<MenuItem>().First();
Check(((string?)splitItem.Header)?.Contains("2 行") == true, "split menu reports batch row count");
splitItem.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.MenuItem.ClickEvent));
Dispatcher.UIThread.RunJobs();
int before = rowsView.RowCount;
var realignButton = window.GetVisualDescendants().OfType<Button>().First(b => (ToolTip.GetTip(b) as string)?.Contains(ModularDiff.Desktop.Lang.Get("md_tip_realign").Split(':')[0]) == true);
if (realignButton is null) { Console.WriteLine("LANGDBG packs=" + ModularDiff.Desktop.Lang.Available.Count + " cur=" + ModularDiff.Desktop.Lang.CurrentName + " want=" + ModularDiff.Desktop.Lang.Get("md_tip_realign").Split(':')[0] + " tips=" + string.Join("§", window.GetVisualDescendants().OfType<Button>().Select(b => ToolTip.GetTip(b) as string).Where(t => t is not null).Take(8))); throw new Exception("realign button not found"); }
realignButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
var batchDone = DateTime.UtcNow.AddSeconds(5);
while (DateTime.UtcNow < batchDone) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Thread.Sleep(10); }
Check(rowsView.RowCount - before == 2, $"batch split realigns to {rowsView.RowCount - before} extra rows (expected 2)");
// ⚡ 自动重对齐:开启后右键换行立即重算,无需点 ⟳。
var autoToggle = window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(b => (b.Content as string) == "⚡");
autoToggle.IsChecked = true;
var gapRow = rowsView.Rows.First(r => r.Left?.Text.StartsWith("--gap:") == true);
rowsView.SelectRows([gapRow]);
var gapItem = window.CreateSplitMenu(gapRow, false)!.Items.OfType<MenuItem>().First();
int autoBefore = rowsView.RowCount;
gapItem.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.MenuItem.ClickEvent));
Dispatcher.UIThread.RunJobs();
Check(rowsView.RowCount - autoBefore == 1, $"auto-realign split adds 1 row immediately (got {rowsView.RowCount - autoBefore})");
foreach (var save in window.GetVisualDescendants().OfType<Button>().Where(b => (ToolTip.GetTip(b) as string)?.StartsWith("保存此侧") == true))
    save.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
var saveDone = DateTime.UtcNow.AddSeconds(5);
while (DateTime.UtcNow < saveDone) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
// 性能回归:超长行(压缩行)不得让渲染/进出编辑卡秒级 —— 绘制只排版可见前缀。
string uiRoot2 = Path.Combine(Path.GetTempPath(), "ModularDiff-perf-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(uiRoot2);
string longLeft = new string('a', 100_000), longRight = new string('b', 100_000);
File.WriteAllText(Path.Combine(uiRoot2, "big-l.txt"), "short\n" + longLeft + "\nshort2\n");
File.WriteAllText(Path.Combine(uiRoot2, "big-r.txt"), "short\n" + longRight + "\nshort2\n");
var perfWindow = new MainWindow(["--plugins", pluginsPath, "--left", Path.Combine(uiRoot2, "big-l.txt"), "--right", Path.Combine(uiRoot2, "big-r.txt"), "--auto-compare"]);
perfWindow.Show();
var perfReady = DateTime.UtcNow.AddSeconds(15);
while (DateTime.UtcNow < perfReady && !perfWindow.GetVisualDescendants().OfType<AlignedRowsView>().Any(v => v.RowCount > 0))
{ Dispatcher.UIThread.RunJobs(); perfWindow.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Thread.Sleep(10); }
var perfView = perfWindow.GetVisualDescendants().OfType<AlignedRowsView>().Single();
Check(perfView.RowCount > 0, "long-line window ready");
var stopwatch = System.Diagnostics.Stopwatch.StartNew();
for (int cycle = 0; cycle < 5; cycle++)
{
    perfView.SelectRows([perfView.Rows[0]]);
    Dispatcher.UIThread.RunJobs(); perfWindow.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    perfWindow.BeginEditRow(0, true);
    Dispatcher.UIThread.RunJobs(); perfWindow.UpdateLayout();
    perfWindow.BeginEditRow(2, false);   // 切换到另一行 = 提交上一格并重定位
    Dispatcher.UIThread.RunJobs(); perfWindow.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
}
stopwatch.Stop();
Check(stopwatch.ElapsedMilliseconds < 1500, $"5 edit cycles with 100k-char lines took {stopwatch.ElapsedMilliseconds}ms (expected <1500ms)");
perfWindow.Close();
Directory.Delete(uiRoot2, recursive: true);
// 目录页多选批量复制:选中的文件复制到另一侧,完成后刷新并恢复选中。
string folderRoot = Path.Combine(Path.GetTempPath(), "ModularDiff-folder-" + Guid.NewGuid().ToString("N"));
string folderLeft = Path.Combine(folderRoot, "L"), folderRight = Path.Combine(folderRoot, "R");
Directory.CreateDirectory(folderLeft); Directory.CreateDirectory(folderRight);
try
{
    File.WriteAllText(Path.Combine(folderLeft, "same.txt"), "one\n");
    File.WriteAllText(Path.Combine(folderRight, "same.txt"), "one\n");
    File.WriteAllText(Path.Combine(folderLeft, "diff.txt"), "left\n");
    File.WriteAllText(Path.Combine(folderRight, "diff.txt"), "right\n");
    File.WriteAllText(Path.Combine(folderLeft, "only.txt"), "only-left\n");
    var folderWindow = new MainWindow(["--plugins", pluginsPath, "--left", folderLeft, "--right", folderRight, "--auto-compare"]);
    folderWindow.Show();
    var folderReady = DateTime.UtcNow.AddSeconds(10);
    while (DateTime.UtcNow < folderReady) { Dispatcher.UIThread.RunJobs(); folderWindow.UpdateLayout(); Thread.Sleep(10);
        if (folderWindow.GetVisualDescendants().OfType<ListBox>().Any(l => l.Items.Count > 0)) break; }
    folderWindow.SelectFolderFiles("diff.txt", "only.txt");
    var copyTask = folderWindow.CopySelectedFolderFiles(true);   // 不能直接 await:后续体要回 UI 上下文,需循环泵
    var folderDone = DateTime.UtcNow.AddSeconds(10);
    while (!copyTask.IsCompleted && DateTime.UtcNow < folderDone) { Dispatcher.UIThread.RunJobs(); folderWindow.UpdateLayout(); Thread.Sleep(10); }
    Check(File.ReadAllText(Path.Combine(folderRight, "diff.txt")) == "left\n", "folder batch copy overwrites target");
    Check(File.Exists(Path.Combine(folderRight, "only.txt")), "folder batch copy creates missing target");
    var folderStatus = string.Join(" ", ((Grid)folderWindow.Content!).Children.OfType<TextBlock>().Select(t => t.Text));
    Check(folderStatus.Contains("/") || folderStatus.Length > 0, $"folder batch copy reports outcome (status: {folderStatus[..Math.Min(60, folderStatus.Length)]})");
    folderWindow.Close();
}
finally { Directory.Delete(folderRoot, recursive: true); }
// 编辑操作对等:转换 / 书签 / 布局切换 / 复制行到对侧(独立小窗口,全部同步 API)。
string opsRoot = Path.Combine(Path.GetTempPath(), "ModularDiff-ops-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(opsRoot);
try
{
    File.WriteAllText(Path.Combine(opsRoot, "l.txt"), "keep\n\tindented\ntrail \ntail\n");
    File.WriteAllText(Path.Combine(opsRoot, "r.txt"), "keep\n    spaced\nplain\ntail\n");
    var opsWindow = new MainWindow(["--plugins", pluginsPath, "--left", Path.Combine(opsRoot, "l.txt"), "--right", Path.Combine(opsRoot, "r.txt"), "--auto-compare"]);
    opsWindow.Show();
    var opsReady = DateTime.UtcNow.AddSeconds(10);
    while (DateTime.UtcNow < opsReady) { Dispatcher.UIThread.RunJobs(); opsWindow.UpdateLayout(); Thread.Sleep(10);
        if (opsWindow.GetVisualDescendants().OfType<AlignedRowsView>().Any(v => v.RowCount > 0)) break; }
    var opsView = opsWindow.GetVisualDescendants().OfType<AlignedRowsView>().Single();
    var opsRealign = opsWindow.GetVisualDescendants().OfType<Button>().First(b => (ToolTip.GetTip(b) as string)?.Contains(ModularDiff.Desktop.Lang.Get("md_tip_realign").Split(':')[0]) == true);
    void OpsRealign() { opsRealign.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); var until = DateTime.UtcNow.AddSeconds(5); while (DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); opsWindow.UpdateLayout(); Thread.Sleep(10); } }
    // 文本转换走插件发现(.txt → 通配 GenericFileTools):Tab → 空格、修剪行尾空白
    var opsLabels = opsWindow.FileTypeActionLabels(true);
    Check(opsLabels.Any(l => l.Contains("Tab → 空格")) && opsLabels.Any(l => l.Contains("修剪")), $"generic transforms discovered from plugin ({string.Join(",", opsLabels)})");
    opsWindow.RunFileTypeAction(true, opsLabels.Select((l, i) => (l, i)).First(t => t.l.Contains("Tab → 空格")).i);
    OpsRealign();
    Check(opsView.Rows.Any(r => r.Left?.Text == "    indented"), "tabs-to-spaces transform lands on left side");
    // 修剪行尾空白
    opsWindow.RunFileTypeAction(true, opsLabels.Select((l, i) => (l, i)).First(t => t.l.Contains("修剪")).i);
    OpsRealign();
    Check(!opsView.Rows.Any(r => r.Left?.Text == "trail "), "trailing whitespace trimmed");
    // 布局切换:上下分栏时右侧行带在左侧行带下方
    opsView.Vertical = true;
    Check(Math.Abs(opsView.CellRect(0, false).Y - AlignedRowsView.RowHeight) < 0.01 && Math.Abs(opsView.CellRect(0, true).Y) < 0.01, "vertical layout stacks bands");
    opsView.Vertical = false;
    // 书签:设 ⌘⇧3 → ⌘3 跳转选中
    opsView.SelectRows([opsView.Rows[^1]]);
    opsView.ToggleBookmark(opsView.RowCount - 1, 3);
    Check(opsView.GoToBookmark(3) && opsView.PrimarySelected == opsView.RowCount - 1, "bookmark set and goto selects row");
    // 复制行到对侧:把左侧 "    indented" 行复制到右侧,重对齐后右侧出现同文本行
    var sourceRow = opsView.Rows.First(r => r.Left?.Text == "    indented");
    opsWindow.CopyToSide(sourceRow, fromLeft: true);
    OpsRealign();
    Check(opsView.Rows.Any(r => r.Right?.Text == "    indented"), "copy line to other side lands after realign");
    // 保存两侧后关闭(dirty 会话避免关闭弹窗)
    foreach (var save in opsWindow.GetVisualDescendants().OfType<Button>().Where(b => (ToolTip.GetTip(b) as string)?.StartsWith("保存此侧") == true))
        save.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    var opsSave = DateTime.UtcNow.AddSeconds(5);
    while (DateTime.UtcNow < opsSave) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
    opsWindow.Close();
}
finally { Directory.Delete(opsRoot, recursive: true); }
// Align With/Isolate + 文件类型操作 + 十六进制就地编辑(程序化钩子)。
string ftRoot = Path.Combine(Path.GetTempPath(), "ModularDiff-ft-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(ftRoot);
try
{
    // 对齐提示:默认相似配对 p↔P/q↔Q,提示后强制 p↔Q。
    File.WriteAllText(Path.Combine(ftRoot, "al.txt"), "a\np\nq\nz\n");
    File.WriteAllText(Path.Combine(ftRoot, "ar.txt"), "a\nP\nQ\nz\n");
    var alignWindow = new MainWindow(["--plugins", pluginsPath, "--left", Path.Combine(ftRoot, "al.txt"), "--right", Path.Combine(ftRoot, "ar.txt"), "--auto-compare"]);
    alignWindow.Show();
    var alignReady = DateTime.UtcNow.AddSeconds(10);
    while (DateTime.UtcNow < alignReady) { Dispatcher.UIThread.RunJobs(); alignWindow.UpdateLayout(); Thread.Sleep(10);
        if (alignWindow.GetVisualDescendants().OfType<AlignedRowsView>().Any(v => v.RowCount > 0)) break; }
    var alignView = alignWindow.GetVisualDescendants().OfType<AlignedRowsView>().Single();
    var pairedBefore = alignView.Rows.First(r => r.Left?.Text == "p").Right?.Text;
    alignWindow.AddAlignmentHint(2, 3);   // 左行2(p) ↔ 右行3(Q)
    var alignView2 = alignWindow.GetVisualDescendants().OfType<AlignedRowsView>().Single();
    var after = alignView2.Rows.First(r => r.Left?.Text == "p").Right?.Text;
    Check(after == "Q", $"alignment hint re-pairs p with Q (before={pairedBefore}, after={after})");
    foreach (var save in alignWindow.GetVisualDescendants().OfType<Button>().Where(b => (ToolTip.GetTip(b) as string)?.StartsWith("保存此侧") == true))
        save.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    var alignSave = DateTime.UtcNow.AddSeconds(3);
    while (DateTime.UtcNow < alignSave) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
    alignWindow.Close();

    // 文件类型插件:.json 侧出现 JSON 工具;执行"压缩 JSON"改变内容
    File.WriteAllText(Path.Combine(ftRoot, "l.json"), "{\n  \"b\": 1,\n  \"a\": 2\n}\n");
    File.WriteAllText(Path.Combine(ftRoot, "r.json"), "{\"b\":1,\"a\":2}");
    var jsonWindow = new MainWindow(["--plugins", pluginsPath, "--left", Path.Combine(ftRoot, "l.json"), "--right", Path.Combine(ftRoot, "r.json"), "--auto-compare"]);
    jsonWindow.Show();
    var jsonReady = DateTime.UtcNow.AddSeconds(10);
    while (DateTime.UtcNow < jsonReady) { Dispatcher.UIThread.RunJobs(); jsonWindow.UpdateLayout(); Thread.Sleep(10);
        if (jsonWindow.GetVisualDescendants().OfType<AlignedRowsView>().Any(v => v.RowCount > 0)) break; }
    var labels = jsonWindow.FileTypeActionLabels(true);
    Check(labels.Any(l => l.Contains("JSON") && l.Contains("压缩")), $"json filetype actions offered ({string.Join(",", labels)})");
    int compactIndex = labels.Select((l, i) => (l, i)).First(t => t.l.Contains("压缩")).i;
    jsonWindow.RunFileTypeAction(true, compactIndex);
    jsonWindow.GetVisualDescendants().OfType<Button>().First(b => (ToolTip.GetTip(b) as string)?.Contains(ModularDiff.Desktop.Lang.Get("md_tip_realign").Split(':')[0]) == true)
        .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    var jsonRealign = DateTime.UtcNow.AddSeconds(3);
    while (DateTime.UtcNow < jsonRealign) { Dispatcher.UIThread.RunJobs(); jsonWindow.UpdateLayout(); Thread.Sleep(10); }
    var jsonView = jsonWindow.GetVisualDescendants().OfType<AlignedRowsView>().Single();
    Check(jsonView.Rows.Any(r => r.Left?.Text.Trim() == "{\"b\":1,\"a\":2}"), "compact-json transform ran on left side");
    foreach (var save in jsonWindow.GetVisualDescendants().OfType<Button>().Where(b => (ToolTip.GetTip(b) as string)?.StartsWith("保存此侧") == true))
        save.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    var jsonSave = DateTime.UtcNow.AddSeconds(3);
    while (DateTime.UtcNow < jsonSave) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
    jsonWindow.Close();
}
finally { try { Directory.Delete(ftRoot, recursive: true); } catch { } }
// 三方合并窗口 + 语法着色 + 深色主题。
string mergeRoot = Path.Combine(Path.GetTempPath(), "ModularDiff-merge-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(mergeRoot);
try
{
    File.WriteAllText(Path.Combine(mergeRoot, "base.txt"), "common\nkeep\n");
    File.WriteAllText(Path.Combine(mergeRoot, "local.txt"), "common\nmine\n");
    File.WriteAllText(Path.Combine(mergeRoot, "remote.txt"), "common\ntheirs\n");
    File.WriteAllText(Path.Combine(mergeRoot, "merged.txt"), "");
    // K1 回归:按生产路径构造(gitOutputPath 走构造函数,而非直调 SaveToOutput),Close 触发整条接线。
    MergeWindow mergeWindow = new(Path.Combine(mergeRoot, "base.txt"), Path.Combine(mergeRoot, "local.txt"), Path.Combine(mergeRoot, "remote.txt"), plugins, gitOutputPath: Path.Combine(mergeRoot, "merged.txt"));
    mergeWindow.Show();
    var mergeReady = DateTime.UtcNow.AddSeconds(8);
    while (DateTime.UtcNow < mergeReady) { Dispatcher.UIThread.RunJobs(); mergeWindow.UpdateLayout(); Thread.Sleep(10);
        if ((mergeWindow.Content as Grid)?.Children.OfType<TextBox>().Any(t => t.Text?.Contains("<<<<<<<") == true) == true) break; }
    mergeWindow.Repaint();
    Check(mergeWindow.ConflictCount == 1, $"one conflict detected (got {mergeWindow.ConflictCount})");
    // K5 回归:结果区手编(清空)立即重析冲突标记,后续 Take 不再命中陈旧行号而崩溃
    mergeWindow.ResultEditor.Text = "manually rewritten";
    Dispatcher.UIThread.RunJobs();
    Check(mergeWindow.ConflictCount == 0, "manual result edit re-parses conflict markers (stale indices dropped)");
    mergeWindow.Take(useRight: false);   // 无冲突时提示而非越界
    await MergeWindowFlow(mergeWindow, Path.Combine(mergeRoot, "merged.txt"));   // 重新走一遍真合并,再验证接线
    async Task MergeWindowFlow(MergeWindow window, string mergedPath)
    {
        var mergeTask = window.MergeAsync();   // 不能直接 await:无调度泵环境下真异步 IO 会死锁,泵循环驱动
        var mergePump = DateTime.UtcNow.AddSeconds(5);
        while (!mergeTask.IsCompleted && DateTime.UtcNow < mergePump) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Dispatcher.UIThread.RunJobs();
        Check(window.ConflictCount == 1, "re-merge after manual edit finds the conflict again");
        window.Take(useRight: false);   // 采用左侧(mine)
        Check(window.ConflictCount == 0, "conflict resolved by taking left");
        bool? completion = null;
        window.GitMergeCompleted += resolved => completion = resolved;
        window.Close();   // 经 Closing 接线写 MERGED(K1:此前 init-only 死接线时这里什么都不写)
        var wait = DateTime.UtcNow.AddSeconds(3);
        while (completion is null && DateTime.UtcNow < wait) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Check(File.ReadAllText(mergedPath).Replace("\r\n", "\n").TrimEnd('\n') + "\n" == "common\nmine\n", "git merge output written via Closing wiring (took left)");
        Check(completion == true, "GitMergeCompleted reports conflict-free through Closing wiring");
    }

    // M1 回归:从未执行合并就关闭(窗口从未 Show,Opened 自动合并未发生)→ fail-closed,MERGED 原样保留
    File.WriteAllText(Path.Combine(mergeRoot, "merged2.txt"), "<<<<<<< git conflict version\n");
    MergeWindow earlyWindow = new(Path.Combine(mergeRoot, "base.txt"), Path.Combine(mergeRoot, "local.txt"), Path.Combine(mergeRoot, "remote.txt"), plugins, gitOutputPath: Path.Combine(mergeRoot, "merged2.txt"));
    bool? earlyCompletion = null;
    earlyWindow.GitMergeCompleted += resolved => earlyCompletion = resolved;
    earlyWindow.Close();   // 同样走 Closing 接线
    var earlyWait = DateTime.UtcNow.AddSeconds(3);
    while (earlyCompletion is null && DateTime.UtcNow < earlyWait) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
    Check(earlyCompletion == false && File.ReadAllText(Path.Combine(mergeRoot, "merged2.txt")) == "<<<<<<< git conflict version\n",
        "premature close is fail-closed through Closing wiring: MERGED untouched, exit state unresolved");

    // 语法着色接线:.cs 侧有 provider,函数返回关键字令牌
    File.WriteAllText(Path.Combine(mergeRoot, "a.cs"), "class A { string s; }\n");
    File.WriteAllText(Path.Combine(mergeRoot, "b.cs"), "class B { int i; }\n");
    var syntaxWindow = new MainWindow(["--plugins", pluginsPath, "--left", Path.Combine(mergeRoot, "a.cs"), "--right", Path.Combine(mergeRoot, "b.cs"), "--auto-compare"]);
    syntaxWindow.Show();
    var syntaxReady = DateTime.UtcNow.AddSeconds(8);
    while (DateTime.UtcNow < syntaxReady) { Dispatcher.UIThread.RunJobs(); syntaxWindow.UpdateLayout(); Thread.Sleep(10);
        if (syntaxWindow.GetVisualDescendants().OfType<AlignedRowsView>().Any(v => v.RowCount > 0)) break; }
    var syntaxView = syntaxWindow.GetVisualDescendants().OfType<AlignedRowsView>().Single();
    Check(syntaxView.SyntaxLeft is not null, "syntax provider wired for .cs side");
    var tokens = syntaxView.SyntaxLeft!("class A { string s; }");
    Check(tokens is { Count: > 0 } && tokens.Any(t => t.Kind == SyntaxTokenKind.Keyword), "csharp highlighter returns keyword tokens");
    // 深色主题切换:调色板翻转 + 持久化
    bool wasDark = ModularDiff.Desktop.Palette.IsDark;
    ModularDiff.Desktop.Palette.SetDark(!wasDark);
    Check(ModularDiff.Desktop.Palette.IsDark != wasDark, "palette toggles");
    // TH1 回归:颜色取自声明式 theme-dark.json(与 WPF 同文件,#4A4A38),而非内置兜底(#3B2E20)
    ModularDiff.Desktop.Palette.SetDark(true);
    string darkModified = ModularDiff.Desktop.Palette.Modified.ToString() ?? "";
    Check(darkModified.Contains("4A4A38", StringComparison.OrdinalIgnoreCase), $"dark palette colors come from theme-dark.json (got {darkModified})");
    string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModularDiff", "Settings.json");
    Check(File.Exists(settingsPath), "theme persisted to settings.json");
    ModularDiff.Desktop.Palette.SetDark(wasDark);   // 还原
    Dispatcher.UIThread.RunJobs(); syntaxWindow.UpdateLayout();
    syntaxWindow.Close();
}
finally { Directory.Delete(mergeRoot, recursive: true); }
// 多语言 + 通配符文件工具。
Check(ModularDiff.Desktop.Lang.Available.Count >= 4, $"language packs loaded ({string.Join(",", ModularDiff.Desktop.Lang.Available)})");
string chineseTitle = ModularDiff.Desktop.Lang.Get("md_title");
ModularDiff.Desktop.Lang.Apply("English");
Check(ModularDiff.Desktop.Lang.Get("md_title") != chineseTitle && ModularDiff.Desktop.Lang.Get("md_title").Contains("Cross-platform"), "language switch translates UI keys");
ModularDiff.Desktop.Lang.Apply("中文");
Check(ModularDiff.Desktop.Lang.Get("md_menu_goto_line").Contains("转到行号"), "fallback chain resolves untranslated keys via zh-CN");
string wildRoot = Path.Combine(Path.GetTempPath(), "ModularDiff-wild-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(wildRoot);
try
{
    File.WriteAllText(Path.Combine(wildRoot, "a.txt"), "one  \ntwo\t\n");   // .txt 无专属提供者 → 通配 "*" 生效
    File.WriteAllText(Path.Combine(wildRoot, "b.txt"), "one\ntwo\n");
    var wildWindow = new MainWindow(["--plugins", pluginsPath, "--left", Path.Combine(wildRoot, "a.txt"), "--right", Path.Combine(wildRoot, "b.txt"), "--auto-compare"]);
    wildWindow.Show();
    var wildReady = DateTime.UtcNow.AddSeconds(8);
    while (DateTime.UtcNow < wildReady) { Dispatcher.UIThread.RunJobs(); wildWindow.UpdateLayout(); Thread.Sleep(10);
        if (wildWindow.GetVisualDescendants().OfType<AlignedRowsView>().Any(v => v.RowCount > 0)) break; }
    var wildLabels = wildWindow.FileTypeActionLabels(true);
    Check(wildLabels.Any(l => l.Contains("Any") && l.Contains("修剪")), $"wildcard generic tools offered for .txt ({string.Join(",", wildLabels)})");
    wildWindow.Close();
}
finally { Directory.Delete(wildRoot, recursive: true); }
// 声明式文件加载诊断:塞一个坏语法 JSON → GrammarEngine 报行列,其余正常。
string badGrammarRoot = Path.Combine(Path.GetTempPath(), "ModularDiff-badgrammar-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(badGrammarRoot);
try
{
    foreach (var dir in Directory.GetDirectories(pluginsPath))   // 复制真实插件根(勿硬编码 Unix 路径,Windows 会落到盘符根)
    {
        string target = Path.Combine(badGrammarRoot, Path.GetFileName(dir));
        Directory.CreateDirectory(target);
        foreach (var entry in Directory.GetFileSystemEntries(dir))
        {
            string name = Path.GetFileName(entry);
            if (Directory.Exists(entry)) Directory.CreateDirectory(Path.Combine(target, name));   // grammars 等子目录:建壳,内部文件逐个拷
            else File.Copy(entry, Path.Combine(target, name));
        }
        string grammarSource = Path.Combine(dir, "grammars");
        if (Directory.Exists(grammarSource))
            foreach (var file in Directory.GetFiles(grammarSource, "*.json"))
                File.Copy(file, Path.Combine(target, "grammars", Path.GetFileName(file)));
    }
    File.WriteAllText(Path.Combine(badGrammarRoot, "ModularDiff.Syntax.Engine", "grammars", "grammar-broken.json"),
        "{\n  \"language\": \"Broken\", \"extensions\": [\".broken\"],\n  \"rules\": [ { \"pattern\": \"[unclosed\", \"kind\": \"Keyword\" } ]\n}");
    using (var badCatalog = PluginCatalog.Load(badGrammarRoot))
    {
        Check(badCatalog.Diagnostics.Count == 0, $"broken grammar does not kill the package ({string.Join(";", badCatalog.Diagnostics)})");
        var engine = badCatalog.GetAll<ISyntaxGrammar>().Single();
        Check((engine.Tokenize("class Calculator {}", ".cs")?.Count ?? 0) > 0, "other grammars still tokenize alongside the broken one");
        Check(engine.LoadDiagnostics.Any(d => d.Contains("grammar-broken.json")), $"diagnostics name the broken file ({string.Join(",", engine.LoadDiagnostics)})");
    }
}
finally { try { Directory.Delete(badGrammarRoot, recursive: true); } catch { } }   // Windows 下已加载插件 DLL 仍锁定,删除失败不判失败
// ─── 对抗性用例:退化路径、边界条件、生命周期 ───
// A1 重对齐后编辑覆盖层必须收起(行集合被替换,悬空覆盖层是缺陷)
var adversarial = window.GetVisualDescendants().OfType<AlignedRowsView>().Single();
window.BeginEditRow(0, true);
Dispatcher.UIThread.RunJobs();
var overlayWhileEditing = window.GetVisualDescendants().OfType<TextBox>().Any(t => t.Name == "CellEditor" && t.IsVisible);
window.GetVisualDescendants().OfType<Button>().First(b => (ToolTip.GetTip(b) as string)?.Contains(ModularDiff.Desktop.Lang.Get("md_tip_realign").Split(':')[0]) == true)
    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
var advPump = DateTime.UtcNow.AddSeconds(3);
while (DateTime.UtcNow < advPump) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Thread.Sleep(10); }
Check(overlayWhileEditing && !window.GetVisualDescendants().OfType<TextBox>().Any(t => t.Name == "CellEditor" && t.IsVisible),
    "editor overlay collapses after realign replaces rows");
// A2 BeginEditRow 指向幽灵行(对侧缺失)必须无操作且不崩
int ghostIndex = Enumerable.Range(0, adversarial.RowCount).FirstOrDefault(i => adversarial.Rows[i].Right is null, -1);
if (ghostIndex >= 0)
{
    window.BeginEditRow(ghostIndex, false);
    Check(!window.GetVisualDescendants().OfType<TextBox>().Any(t => t.Name == "CellEditor" && t.IsVisible), "begin-edit on ghost row is a no-op");
}
// A3 越界索引/幽灵侧行号不崩
window.BeginEditRow(99999, true);
adversarial.SelectRows([]);
// A4 CRLF 保留:换行插件拆 CRLF 行 → 新行 Ending 仍为 CRLF
string crlfRoot = Path.Combine(Path.GetTempPath(), "ModularDiff-crlf-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(crlfRoot);
try
{
    File.WriteAllText(Path.Combine(crlfRoot, "l.css"), "body{margin:0;color:red;}\r\n");
    File.WriteAllText(Path.Combine(crlfRoot, "r.css"), "body{\r\nmargin:0;\r\ncolor:red;\r\n}\r\n");
    var crlfWindow = new MainWindow(["--plugins", pluginsPath, "--left", Path.Combine(crlfRoot, "l.css"), "--right", Path.Combine(crlfRoot, "r.css"), "--auto-compare"]);
    crlfWindow.Show();
    var crlfReady = DateTime.UtcNow.AddSeconds(8);
    while (DateTime.UtcNow < crlfReady) { Dispatcher.UIThread.RunJobs(); crlfWindow.UpdateLayout(); Thread.Sleep(10);
        if (crlfWindow.GetVisualDescendants().OfType<AlignedRowsView>().Any(v => v.RowCount > 0)) break; }
    var crlfView = crlfWindow.GetVisualDescendants().OfType<AlignedRowsView>().Single();
    var crlfRow = crlfView.Rows.First(r => r.Left?.Text.Contains(";") == true && r.Left.Text.Contains("{"));
    crlfView.SelectRows([crlfRow]);
    crlfWindow.CreateSplitMenu(crlfRow, true)!.Items.OfType<MenuItem>().First()
        .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.MenuItem.ClickEvent));
    Dispatcher.UIThread.RunJobs();
    crlfWindow.GetVisualDescendants().OfType<Button>().First(b => (ToolTip.GetTip(b) as string)?.Contains(ModularDiff.Desktop.Lang.Get("md_tip_realign").Split(':')[0]) == true)
        .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    var crlfDone = DateTime.UtcNow.AddSeconds(3);
    while (DateTime.UtcNow < crlfDone) { Dispatcher.UIThread.RunJobs(); crlfWindow.UpdateLayout(); Thread.Sleep(10); }
    var after = crlfWindow.GetVisualDescendants().OfType<AlignedRowsView>().Single();
    Check(after.Rows.Count(r => r.Left?.Text == "margin:0;") == 1 && after.Rows.First(r => r.Left?.Text == "margin:0;").Left!.Ending == "CRLF",
        "css split preserves CRLF endings");
    crlfWindow.Close();
}
finally { Directory.Delete(crlfRoot, recursive: true); }
// A5 CopyToSide 目标为 CRLF 时追加 CR(回归 C3 修复)
string mixRoot = Path.Combine(Path.GetTempPath(), "ModularDiff-mix-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(mixRoot);
try
{
    File.WriteAllText(Path.Combine(mixRoot, "lf.txt"), "same\nleft line\n");
    File.WriteAllText(Path.Combine(mixRoot, "crlf.txt"), "same\r\ndifferent\r\n");
    var mixWindow = new MainWindow(["--plugins", pluginsPath, "--left", Path.Combine(mixRoot, "lf.txt"), "--right", Path.Combine(mixRoot, "crlf.txt"), "--auto-compare"]);
    mixWindow.Show();
    var mixReady = DateTime.UtcNow.AddSeconds(8);
    while (DateTime.UtcNow < mixReady) { Dispatcher.UIThread.RunJobs(); mixWindow.UpdateLayout(); Thread.Sleep(10);
        if (mixWindow.GetVisualDescendants().OfType<AlignedRowsView>().Any(v => v.RowCount > 0)) break; }
    var mixView = mixWindow.GetVisualDescendants().OfType<AlignedRowsView>().Single();
    var mixRow = mixView.Rows.First(r => r.Left?.Text == "left line");
    mixView.SelectRows([mixRow]);
    mixWindow.CopyToSide(mixRow, fromLeft: true);
    Dispatcher.UIThread.RunJobs();
    mixWindow.GetVisualDescendants().OfType<Button>().First(b => (ToolTip.GetTip(b) as string)?.Contains(ModularDiff.Desktop.Lang.Get("md_tip_realign").Split(':')[0]) == true)
        .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    var mixDone = DateTime.UtcNow.AddSeconds(3);
    while (DateTime.UtcNow < mixDone) { Dispatcher.UIThread.RunJobs(); mixWindow.UpdateLayout(); Thread.Sleep(10); }
    var mixRows = mixWindow.GetVisualDescendants().OfType<AlignedRowsView>().Single().Rows;
    Check(mixRows.Any(r => r.Right?.Text == "left line" && r.Right.Ending == "CRLF"),
        "copy-to-side appends CR when target uses CRLF");
    mixWindow.Close();
}
finally { Directory.Delete(mixRoot, recursive: true); }
// A6 MergeWindow 退化路径:未合并点采用/路径不存在 → 状态提示,不崩不写盘
var degenerate = new MergeWindow(null, null, null, plugins);
degenerate.Show(); Dispatcher.UIThread.RunJobs();
degenerate.Take(true);   // 无冲突可决策
degenerate.Repaint();
Check(degenerate.ConflictCount == 0, "take-without-conflict is a no-op");
degenerate.Close();
// A7 跨行选区含幽灵行:切片以空行占位,行数守恒
var ghostSel = window.GetVisualDescendants().OfType<AlignedRowsView>().Single();
var withGhost = Enumerable.Range(0, ghostSel.RowCount).FirstOrDefault(i => ghostSel.Rows[i].Right is null, -1);
if (withGhost >= 0)
{
    ghostSel.SetTextSelection(0, 0, withGhost, 3, true);
    var ghostSliced = ghostSel.SelectedText();
    Check(ghostSliced.Split('\n').Length == withGhost + 1, $"ghost rows keep line count in selection slice ({sliced.Split('\n').Length} vs {withGhost + 1})");
    ghostSel.ClearTextSelection();
}
// 十六进制视图:二进制对 → hex 行渲染且状态栏提示。
string binRoot = Path.Combine(Path.GetTempPath(), "ModularDiff-bin-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(binRoot);
try
{
    File.WriteAllBytes(Path.Combine(binRoot, "a.bin"), new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0xFF, 0xFE, 0x1A, 0xE9, 0x80, 0x11, 0xB8 });
    File.WriteAllBytes(Path.Combine(binRoot, "b.bin"), new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0xFF, 0xFE, 0x7B, 0xE9, 0x80, 0x11, 0xB8 });
    var hexWindow = new MainWindow(["--plugins", pluginsPath, "--left", Path.Combine(binRoot, "a.bin"), "--right", Path.Combine(binRoot, "b.bin"), "--auto-compare"]);
    hexWindow.Show();
    var hexDone = DateTime.UtcNow.AddSeconds(10);
    while (DateTime.UtcNow < hexDone) { Dispatcher.UIThread.RunJobs(); hexWindow.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Thread.Sleep(10);
        if (hexWindow.GetVisualDescendants().OfType<AlignedRowsView>().Any(v => v.RowCount > 0)) break; }
    var hexStatus = string.Join(" ", ((Grid)hexWindow.Content!).Children.OfType<TextBlock>().Select(t => t.Text));
    Check(hexWindow.GetVisualDescendants().OfType<AlignedRowsView>().Any(v => v.RowCount > 0) && hexStatus.Contains("十六进制"), "binary pair renders hex view");
    // 就地编辑:把左侧第 8 字节改成与右侧相同的 0x7b → 保存 → 视图内重新比较后两文件一致
    hexWindow.BeginEditRow(0, true);
    var hexEditor = hexWindow.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "CellEditor" && t.IsVisible);
    Check(hexEditor is not null, "hex inline editor visible");
    hexEditor!.Text = "4d 5a 90 00 03 ff fe 7b e9 80 11 b8";
    Dispatcher.UIThread.RunJobs();
    Check(hexWindow.SaveHexEdit(), "hex save reports success");
    Check(File.ReadAllBytes(Path.Combine(binRoot, "a.bin"))[7] == 0x7b, "hex edit written to disk");
    // 保存后的重新比较由菜单处理器链式执行;无头直调路径下用 ▶ 触发同一 Compare
    var hexCompareButton = ((Grid)hexWindow.Content!).Children.OfType<StackPanel>().Single(p => p.Orientation == Avalonia.Layout.Orientation.Horizontal && p.Children.OfType<Button>().Any()).Children.OfType<Button>().First();
    hexCompareButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    var hexRelist = DateTime.UtcNow.AddSeconds(5);
    while (DateTime.UtcNow < hexRelist) { Dispatcher.UIThread.RunJobs(); hexWindow.UpdateLayout(); Thread.Sleep(10); }
    var identicalStatus = string.Join(" ", ((Grid)hexWindow.Content!).Children.OfType<TextBlock>().Select(t => t.Text));
    Check(identicalStatus.Contains("相同"), $"reloaded hex shows identical (status: {identicalStatus[..Math.Min(70, identicalStatus.Length)]})");
    hexWindow.Close();

    // K2 回归:超过 1MB 的文件,hex 编辑保存必须拼回尾部 —— 文件长度不变,绝不允许截断到 1MB
    string bigLeft = Path.Combine(binRoot, "big-l.bin"), bigRight = Path.Combine(binRoot, "big-r.bin");
    var big = new byte[1_500_000]; for (int i = 0; i < big.Length; i++) big[i] = (byte)(i % 251);
    File.WriteAllBytes(bigLeft, big); File.WriteAllBytes(bigRight, big);
    var bigWindow = new MainWindow(["--plugins", pluginsPath, "--left", bigLeft, "--right", bigRight, "--auto-compare"]);
    bigWindow.Show();
    var bigReady = DateTime.UtcNow.AddSeconds(15);
    while (DateTime.UtcNow < bigReady) { Dispatcher.UIThread.RunJobs(); bigWindow.UpdateLayout(); Thread.Sleep(10);
        if (bigWindow.GetVisualDescendants().OfType<AlignedRowsView>().Any(v => v.RowCount > 0)) break; }
    bigWindow.BeginEditRow(0, true);
    var bigEditor = bigWindow.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "CellEditor" && t.IsVisible);
    bigEditor!.Text = "ff 01 02 03 04 05 06 07 08 09 0a 0b 0c 0d 0e 0f";
    Dispatcher.UIThread.RunJobs();
    bool bigSavedOk = bigWindow.SaveHexEdit();
    var bigStatus = string.Join(" ", ((Grid)bigWindow.Content!).Children.OfType<TextBlock>().Select(t => t.Text));
    Check(bigSavedOk, $"oversized hex save reports success (status: {bigStatus})");
    var bigSaved = File.ReadAllBytes(bigLeft);
    Check(bigSaved.Length == 1_500_000, $"oversized hex save preserves file length (got {bigSaved.Length}, expected 1500000)");
    Check(bigSaved[0] == 0xff && bigSaved[15] == 0x0f && bigSaved[1_499_999] == (byte)(1_499_999 % 251), "edited prefix + untouched tail both present");
    bigWindow.Close();
}
finally { Directory.Delete(binRoot, recursive: true); }
// 图片视图:BMP 对(一枚差异像素)→ 图片比较页。
static byte[] Bmp(int width, int height, byte different)
{
    int row = (width * 3 + 3) / 4 * 4, pixels = row * height;
    var data = new byte[54 + pixels];
    data[0] = (byte)'B'; data[1] = (byte)'M';
    BitConverter.GetBytes(54 + pixels).CopyTo(data, 2);
    BitConverter.GetBytes(54).CopyTo(data, 10);
    BitConverter.GetBytes(40).CopyTo(data, 14);
    BitConverter.GetBytes(width).CopyTo(data, 18);
    BitConverter.GetBytes(height).CopyTo(data, 22);
    data[26] = 1; data[28] = 24;
    for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
    {
        int at = 54 + y * row + x * 3;
        data[at] = different; data[at + 1] = 32; data[at + 2] = 200;
    }
    return data;
}
string picRoot = Path.Combine(Path.GetTempPath(), "ModularDiff-pic-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(picRoot);
try
{
    File.WriteAllBytes(Path.Combine(picRoot, "a.bmp"), Bmp(3, 3, 0));
    File.WriteAllBytes(Path.Combine(picRoot, "b.bmp"), Bmp(3, 3, 90));
    var picWindow = new MainWindow(["--plugins", pluginsPath, "--left", Path.Combine(picRoot, "a.bmp"), "--right", Path.Combine(picRoot, "b.bmp"), "--auto-compare"]);
    picWindow.Show();
    var picDone = DateTime.UtcNow.AddSeconds(10);
    while (DateTime.UtcNow < picDone) { Dispatcher.UIThread.RunJobs(); picWindow.UpdateLayout(); Thread.Sleep(10); }
    var picStatus = string.Join(" ", ((Grid)picWindow.Content!).Children.OfType<TextBlock>().Select(t => t.Text));
    Check(picStatus.Contains("图片比较"), $"picture pair opens picture view (status: {picStatus[..Math.Min(80, picStatus.Length)]})");
    picWindow.Close();
}
finally { Directory.Delete(picRoot, recursive: true); }
// 注意:在无 Avalonia 调度泵的环境里 await 真异步 IO 会死锁,故同步等待脱离 UI 上下文的后台任务。
Task.Run(async () =>
{
    string eolRoot = Path.Combine(Path.GetTempPath(), "ModularDiff-eol-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(eolRoot);
    try
    {
        string eolLeft = Path.Combine(eolRoot, "a.txt"), eolRight = Path.Combine(eolRoot, "b.txt");
        await File.WriteAllTextAsync(eolLeft, "same\r\nrest\r\n"); await File.WriteAllTextAsync(eolRight, "same\nrest\n");
        var eolSession = new ComparisonSession(plugins); await eolSession.LoadAsync(eolLeft, eolRight);
        Check(eolSession.Presentation!.Hunks.Count > 0, "EOL-only differences reported by default");
        eolSession.Options = new(IgnoreLineEndings: true); eolSession.Recompare();
        Check(eolSession.Presentation!.Hunks.Count == 0, "ignore-EOL option collapses EOL-only hunks");
    }
    finally { Directory.Delete(eolRoot, recursive: true); }
}).GetAwaiter().GetResult();
SynchronizationContext.SetSynchronizationContext(null);
string root = Path.Combine(Path.GetTempPath(), "ModularDiff-cross-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    string left = Path.Combine(root, "左 目录"), right = Path.Combine(root, "右 目录");
    Directory.CreateDirectory(left); Directory.CreateDirectory(right);
    string a = Path.Combine(left, "你好.cs"), b = Path.Combine(right, "你好.cs");
    await File.WriteAllTextAsync(a, "same\r\nleft\r\n"); await File.WriteAllTextAsync(b, "same\nright\n");
    var session = new ComparisonSession(plugins); await session.LoadAsync(a, b);
    Check(session.Presentation!.Hunks.Count > 0, "aligned difference presentation");
    session.Merge(session.Presentation.Hunks[0].Id, MergeDirection.LeftToRight);
    Check(session.Right!.IsDirty, "block merge modifies destination buffer");
    session.History(false, false); Check(session.Right.Text == "same\nright\n", "merge undo");
    session.History(false, true); Check(session.Right.IsDirty, "merge redo");
string caLeft = Path.Combine(root, "ca-l.txt"), caRight = Path.Combine(root, "ca-r.txt");
await File.WriteAllTextAsync(caLeft, "a\nb\nc\nd\ne\n"); await File.WriteAllTextAsync(caRight, "a\nB\nc\nD\ne\n");
var copyAllSession = new ComparisonSession(plugins); await copyAllSession.LoadAsync(caLeft, caRight);
Check(copyAllSession.Presentation!.Hunks.Count == 2, "copy-all setup has two hunks");
int allCopied = copyAllSession.MergeAll(MergeDirection.LeftToRight);
Check(allCopied == 2 && copyAllSession.Right!.Text == copyAllSession.Left!.Text && copyAllSession.Right.IsDirty, "copy-all lands every hunk (destination equals source)");
    session.Edit(false, "changed\n"); await session.SaveAsync(false);
    Check(await File.ReadAllTextAsync(b) == "changed\n" && !session.Dirty, "save through storage plugin");
    session.Edit(false, "draft\n"); await File.WriteAllTextAsync(b, "external\n");
    bool conflict = false; try { await session.SaveAsync(false); } catch (DiskConflictException) { conflict = true; }
    Check(conflict && await File.ReadAllTextAsync(b) == "external\n", "external modification protected");
    var compare = await new ComparisonEngine(plugins).CompareAsync(new(left, right));
    Check(compare.Files.Count == 1 && compare.HasDifferences, "Unicode directory comparison");
    T Plugin<T>() where T : class, IPlugin => plugins.GetAll<T>().First();
    var plan = await Plugin<ISyncPlanner>().PlanAsync(left, right, ["你好.cs"], SyncMode.LeftToRight);
    Check(await File.ReadAllTextAsync(b) == "external\n", "sync preview has no writes");
    var safety = new SyncSafetyContext(Path.Combine(root, "history"), Plugin<ISyncBackup>(), Plugin<ISyncJournal>());
    var result = await Plugin<ISyncExecutor>().ExecuteAsync(plan, [new("你好.cs", SyncAction.LeftToRight)], safety);
    Check(result.Entries.Single().Outcome == SyncOutcome.Copied, "sync with backup and journal");
    var recovery = Plugin<ISyncRecovery>(); var preview = await recovery.PreviewAsync(safety, result.OperationId!);
    var restored = await recovery.RestoreAsync(safety, result.OperationId!, preview.Items.Where(i => i.CanRestore).Select(i => i.EntryId).ToArray());
    Check(restored.Restored == 1 && await File.ReadAllTextAsync(b) == "external\n", "sync recovery restores original bytes");
    Console.WriteLine("All cross-platform smoke tests passed.");
}
finally { Directory.Delete(root, recursive: true); }
