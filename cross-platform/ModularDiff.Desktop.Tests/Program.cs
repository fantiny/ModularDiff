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
File.WriteAllText(Path.Combine(uiRoot, "left.cs"), "// Comparison sample\nclass Calculator\n{\n    int Sum(int a, int b) => a + b;\n}\n");
File.WriteAllText(Path.Combine(uiRoot, "right.cs"), "// Comparison sample\nclass Calculator\n{\n    // Handle subtraction\n    int Sum(int a, int b) => a - b;\n}\n");
var window = new MainWindow(["--plugins", pluginsPath, "--left", Path.Combine(uiRoot, "left.cs"), "--right", Path.Combine(uiRoot, "right.cs"), "--auto-compare"]);
window.Show();
Check(window.Content is Grid && window.IsVisible, "Avalonia window initializes on headless platform");
var deadline = DateTime.UtcNow.AddSeconds(15);
while (!((Grid)window.Content!).Children.OfType<TabControl>().Single().Items.OfType<TabItem>().Select(t => t.Content).OfType<ListBox>().Any(l => l.Items.Count > 0) && DateTime.UtcNow < deadline)
{ Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Thread.Sleep(10); }
Console.WriteLine(string.Join(" | ", ((Grid)window.Content!).Children.OfType<TextBlock>().Select(t => t.Text)));
Check(((Grid)window.Content!).Children.OfType<TabControl>().Single().Items.OfType<TabItem>().Select(t => t.Content).OfType<ListBox>().Any(l => l.Items.Count > 0), "GUI compare populates aligned rows");
window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
using (var frame = window.CaptureRenderedFrame())
{
    Check(frame is not null, "GUI renders a frame");
    if (args.Length > 1) frame!.Save(args[1]);
}
window.Close();
Directory.Delete(uiRoot, recursive: true);
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
