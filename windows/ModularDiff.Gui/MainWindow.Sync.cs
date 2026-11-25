using System.IO;
using System.Windows;
using System.Windows.Controls;
using ModularDiff.Contracts;

namespace ModularDiff.Gui;

public partial class MainWindow
{
    private CancellationTokenSource? _syncCancellation;
    private bool _syncActive;
    private void InitializeSync()
    {
        InitializeSyncSafety();
        SyncPlannerBox.ItemsSource = _plugins.GetAll<ISyncPlanner>(); SyncPlannerBox.SelectedIndex = 0;
        SyncPreviewBox.ItemsSource = _plugins.GetAll<ISyncPreview>(); SyncPreviewBox.SelectedIndex = 0;
        SyncExecutorBox.ItemsSource = _plugins.GetAll<ISyncExecutor>(); SyncExecutorBox.SelectedIndex = 0;
        Closed += (_, _) => _syncCancellation?.Cancel();
    }
    private IReadOnlyList<string> SyncPaths(bool batch)
    {
        if (_folderResult is null) return [];
        if (batch)
        {
            // Filtering applies to the complete comparison result, independently of tree expansion.
            var nameFilter = CurrentFolderNameFilter(); int filter = FolderFilter.SelectedIndex;
            return _folderResult.Files.Where(f => !f.IsDirectory
                && (filter switch { 1 => f.Status != FileStatus.Equal, 2 => f.Status == FileStatus.Equal, 3 => f.Status == FileStatus.LeftOnly, 4 => f.Status == FileStatus.RightOnly, _ => true })
                && (nameFilter.IsEmpty || nameFilter.Matches(f.RelativePath, Path.GetFileName(f.RelativePath)))).Select(f => f.RelativePath).ToArray();
        }
        // 两侧选择各自独立(不再镜像):同步范围取两格选区的并集 —— 只点某一侧同样有效。
        var nodes = LeftFolders.SelectedItems.OfType<FolderNode>()
            .Concat(RightFolders.SelectedItems.OfType<FolderNode>()).Distinct().ToArray();
        return _folderResult.Files.Where(f => !f.IsDirectory && nodes.Any(n => n.IsDirectory
            ? f.RelativePath.StartsWith(n.RelativePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            : string.Equals(f.RelativePath, n.RelativePath, StringComparison.OrdinalIgnoreCase))).Select(f => f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private async void FolderSyncClick(object sender, RoutedEventArgs e)
    {
        string tag = ((Button)sender).Tag?.ToString() ?? "";
        bool batch = tag.StartsWith("batch");
        var mode = tag.EndsWith("left") ? SyncMode.RightToLeft : tag.EndsWith("right") ? SyncMode.LeftToRight : SyncMode.Bidirectional;
        await PreviewSync(batch, mode);
    }
    private async Task PreviewSync(bool batch, SyncMode mode)
    {
        if (_busy || _folderResult is null || _rootRequest is null || SyncPlannerBox.SelectedItem is not ISyncPlanner planner
            || SyncPreviewBox.SelectedItem is not ISyncPreview preview || SyncExecutorBox.SelectedItem is not ISyncExecutor executor) return;
        var paths = SyncPaths(batch);
        if (paths.Count == 0) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_no_sync_files"); return; }
        if (!await CanLeaveEdits()) return;
        _detailCancellation?.Cancel();
        _syncCancellation?.Dispose(); _syncCancellation = new();
        _syncActive = true; TextView.IsEnabled = false; UpdateEditState();
        _busy = true; CompareButton.IsEnabled = false; CancelButton.IsEnabled = true; ActivityLabel.Text = ModularDiff.Gui.Controls.Lang.Get("activity_sync_preview");
        try
        {
            var result = _folderResult;
            var progress = new Progress<SyncProgress>(p => StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_sync_validate", p.Completed, p.Total, p.RelativePath));
            var plan = await Task.Run(() => planner.PlanAsync(result.LeftPath, result.RightPath, paths, mode, progress, _syncCancellation.Token));
            _syncCancellation.Token.ThrowIfCancellationRequested();
            string scope = ModularDiff.Gui.Controls.Lang.Get(batch ? "sync_scope_batch" : "sync_scope_selected");
            var dialog = new SyncPreviewWindow(plan, preview, executor, scope, SafetyContext) { Owner = this };
            CancelButton.IsEnabled = false; ActivityLabel.Text = ModularDiff.Gui.Controls.Lang.Get("activity_previewing"); dialog.ShowDialog();
            if (dialog.Result is not null)
            {
                await RefreshAfterSync();
                StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_sync_done", dialog.Result.Entries.Count(r => r.Outcome == SyncOutcome.Copied), dialog.Result.Entries.Count(r => r.Outcome == SyncOutcome.Failed), dialog.Result.Entries.Count(r => r.Outcome == SyncOutcome.Cancelled));
            }
        }
        catch (OperationCanceledException) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_sync_plan_cancelled"); }
        catch (Exception ex) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_sync_failed", ModularDiff.Gui.Controls.Lang.Text(ex.Message)); }
        finally { _syncActive = false; TextView.IsEnabled = true; UpdateEditState(); _busy = false; CompareButton.IsEnabled = _plugins.Diagnostics.Count == 0; CancelButton.IsEnabled = false; ActivityLabel.Text = ModularDiff.Gui.Controls.Lang.Get("activity_ready"); }
    }
    private async Task RefreshAfterSync()
    {
        if (_rootRequest is null) return;
        ClearEditors();
        var expanded = _tree?.All.Where(n => n.IsDirectory).ToDictionary(n => n.RelativePath, n => n.IsExpanded);
        string? selected = (LeftFolders.SelectedItem as FolderNode)?.RelativePath;
        var result = await Task.Run(() => _engine.CompareAsync(_rootRequest, "directories"));
        _folderResult = result; _folderResultFinal = true; _tree = await Task.Run(() => FolderTree.Build(result, CancellationToken.None));
        foreach (var node in _tree.All.Where(n => n.IsDirectory)) if (expanded?.TryGetValue(node.RelativePath, out bool value) == true) node.IsExpanded = value;
        _fileResult = null; _textFile = null; _currentNode = null; _presentation = null;
        LeftCode.SetRows([]); RightCode.SetRows([]); Overview.SetPresentation(null);
        RefreshFolderRows(_tree.All.FirstOrDefault(n => n.RelativePath == selected)); SetView(false);
    }
}
