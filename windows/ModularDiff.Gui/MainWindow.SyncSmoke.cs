using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ModularDiff.Contracts;

namespace ModularDiff.Gui;

public partial class MainWindow
{
    private async void SyncSmokeTest(object sender, RoutedEventArgs e)
    {
        int exit = 0;
        string root = Path.Combine(AppContext.BaseDirectory, "sync-smoke-" + Guid.NewGuid().ToString("N"));
        _historyRootOverride = Path.Combine(root, "history");
        SyncPreviewWindow? previewWindow = null;
        SyncHistoryWindow? historyWindow = null;
        try
        {
            LeftPath.Text = Path.Combine(root, "left"); RightPath.Text = Path.Combine(root, "right");
            Directory.CreateDirectory(Path.Combine(LeftPath.Text, "src")); Directory.CreateDirectory(Path.Combine(RightPath.Text, "src"));
            await File.WriteAllTextAsync(Path.Combine(LeftPath.Text, "src", "changed.cs"), "left version\n");
            await File.WriteAllTextAsync(Path.Combine(RightPath.Text, "src", "changed.cs"), "right version\n");
            await File.WriteAllTextAsync(Path.Combine(LeftPath.Text, "src", "added.cs"), "left only\n");
            await File.WriteAllTextAsync(Path.Combine(RightPath.Text, "right-only.txt"), "right only\n");
            await CompareAsync(); Require(_folderResult is not null && _tree is not null, "Folder compare failed");
            LeftFolders.SelectedItems.Clear();
            LeftFolders.SelectedItems.Add(_tree!.All.First(n => n.Name == "changed.cs"));
            LeftFolders.SelectedItems.Add(_tree.All.First(n => n.Name == "right-only.txt"));
            Require(SyncPaths(false).Count == 2 && RightFolders.SelectedItems.Count == 2, "Paired multiselection");
            CollapseAll(this, new RoutedEventArgs()); Require(SyncPaths(true).Count == 3, "Collapsed descendants excluded from batch");
            FolderSearch.Text = "src"; Require(SyncPaths(true).Count == 2, "Filter scope"); FolderSearch.Clear();
            var planner = (ISyncPlanner)SyncPlannerBox.SelectedItem; var presenter = (ISyncPreview)SyncPreviewBox.SelectedItem; var executor = (ISyncExecutor)SyncExecutorBox.SelectedItem;
            var plan = await planner.PlanAsync(LeftPath.Text, RightPath.Text, SyncPaths(true), SyncMode.Bidirectional);
            Require(plan.Items.Single(i => i.RelativePath.EndsWith("changed.cs")).Conflict, "Bidirectional conflict not shown");
            previewWindow = new(plan, presenter, executor, "测试范围：全部 3 个文件，含新增、覆盖与双向补齐", SafetyContext) { Owner = this };
            previewWindow.Show(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(!File.Exists(Path.Combine(RightPath.Text, "src", "added.cs")), "Preview wrote a file");
            previewWindow.Close(); previewWindow = new(plan, presenter, executor, "全部文件 · 同名冲突在操作列选择方向", SafetyContext) { Owner = this };
            previewWindow.Rows.Single(r => r.RelativePath.EndsWith("changed.cs")).Action = SyncAction.LeftToRight;
            previewWindow.Show(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (Arg("--screenshot") is string image)
            {
                previewWindow.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)previewWindow.PreviewRoot.ActualWidth, (int)previewWindow.PreviewRoot.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen()) dc.DrawRectangle(new VisualBrush(previewWindow.PreviewRoot), null, new Rect(0, 0, previewWindow.PreviewRoot.ActualWidth, previewWindow.PreviewRoot.ActualHeight));
                bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.ChangeExtension(image, ".preview.png")); encoder.Save(stream);
            }
            await previewWindow.ExecuteAsync();
            Require(previewWindow.Result?.Entries.Count(r => r.Outcome == SyncOutcome.Copied) == 3, "Preview execution");
            previewWindow.Close(); previewWindow = null;
            await RefreshAfterSync(); Require(_folderResult?.HasDifferences == false, "Directory refresh after sync");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (Arg("--screenshot") is string screenshot) SaveScreenshot(screenshot);
            historyWindow = new(SafetyContext, (ISyncRecovery)SyncRecoveryBox.SelectedItem) { Owner = this };
            historyWindow.Show(); await historyWindow.ReloadAsync(); await historyWindow.PreviewSelectedAsync();
            Require(historyWindow.Rows.Count(r => r.CanRestore) == 3, "Recovery preview did not offer all copied files");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (Arg("--screenshot") is string historyImage)
            {
                historyWindow.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)historyWindow.HistoryRoot.ActualWidth, (int)historyWindow.HistoryRoot.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen()) dc.DrawRectangle(new VisualBrush(historyWindow.HistoryRoot), null, new Rect(0, 0, historyWindow.HistoryRoot.ActualWidth, historyWindow.HistoryRoot.ActualHeight));
                bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.ChangeExtension(historyImage, ".history.png")); encoder.Save(stream);
            }
            await historyWindow.RestoreSelectedAsync();
            Require(await File.ReadAllTextAsync(Path.Combine(RightPath.Text, "src", "changed.cs")) == "right version\n", "Overwrite undo failed");
            Require(!File.Exists(Path.Combine(RightPath.Text, "src", "added.cs")) && !File.Exists(Path.Combine(LeftPath.Text, "right-only.txt")), "New-file undo failed");
            historyWindow.Close(); historyWindow = null;
            await RefreshAfterSync(); Require(_folderResult?.HasDifferences == true, "Recovery did not refresh comparison");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "sync-smoke-result.json"), System.Text.Json.JsonSerializer.Serialize(new { success = true, root, copied = 3, restored = 3 }));
        }
        catch (Exception ex) { exit = 2; File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "sync-smoke-error.txt"), ex.ToString()); }
        finally { previewWindow?.Close(); historyWindow?.Close(); }
        _allowClose = true; Application.Current.Shutdown(exit);
    }
}
