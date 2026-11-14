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
string uiRoot = Path.Combine(Path.GetTempPath(), "ModularDiff-ui-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(uiRoot);
File.WriteAllText(Path.Combine(uiRoot, "left.cs"), "// Comparison sample\nclass Calculator\n{\n    int Sum(int a, int b) => a + b;\n}\n--bg:#0b0d10;--panel:#11151a;\n--line:#2a313b;--code:#0b0b11;\n--gap:8px;--radius:12px;\n");
File.WriteAllText(Path.Combine(uiRoot, "right.cs"), "// Comparison sample\nclass Calculator\n{\n    // Handle subtraction\n    int Sum(int a, int b) => a - b;\n}\n--bg:#0b0d10;--panel:#11151a;\n--line:#2a313b;--code:#0b0b11;\n--gap:8px;--radius:12px;\n");
var window = new MainWindow(["--plugins", pluginsPath, "--left", Path.Combine(uiRoot, "left.cs"), "--right", Path.Combine(uiRoot, "right.cs"), "--auto-compare"]);
window.Show();
Check(window.Content is Grid && window.IsVisible, "Avalonia window initializes on headless platform");
var deadline = DateTime.UtcNow.AddSeconds(15);
while (!(window.GetVisualDescendants().OfType<AlignedRowsView>().Any(v => v.RowCount > 0) && DateTime.UtcNow < deadline))
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
var realignButton = window.GetVisualDescendants().OfType<Button>().First(b => (ToolTip.GetTip(b) as string)?.Contains("重新对齐") == true);
realignButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
var batchDone = DateTime.UtcNow.AddSeconds(5);
while (DateTime.UtcNow < batchDone) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Thread.Sleep(10); }
Check(rowsView.RowCount - before == 2, $"batch split realigns to {rowsView.RowCount - before} extra rows (expected 2)");
// ⚡ 自动重对齐:开启后右键换行立即重算,无需点 ⟳。
var autoToggle = window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(b => (ToolTip.GetTip(b) as string)?.Contains("自动重对齐") == true);
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
    Check(folderStatus.Contains("批量复制完成"), $"folder batch copy reports outcome (status: {folderStatus[..Math.Min(60, folderStatus.Length)]})");
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
    var opsRealign = opsWindow.GetVisualDescendants().OfType<Button>().First(b => (ToolTip.GetTip(b) as string)?.Contains("重新对齐") == true);
    void OpsRealign() { opsRealign.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); var until = DateTime.UtcNow.AddSeconds(5); while (DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); opsWindow.UpdateLayout(); Thread.Sleep(10); } }
    // Tab → 空格(作用于默认聚焦的左侧)
    opsWindow.TransformSide(1);
    OpsRealign();
    Check(opsView.Rows.Any(r => r.Left?.Text == "    indented"), "tabs-to-spaces transform lands on left side");
    // 修剪行尾空白
    opsWindow.TransformSide(0);
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
    hexWindow.Close();
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
