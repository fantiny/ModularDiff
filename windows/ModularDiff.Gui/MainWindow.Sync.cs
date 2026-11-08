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
            string query = FolderSearch.Text.Trim(); int filter = FolderFilter.SelectedIndex;
            return _folderResult.Files.Where(f => (filter switch { 1 => f.Status != FileStatus.Equal, 2 => f.Status == FileStatus.Equal, 3 => f.Status == FileStatus.LeftOnly, 4 => f.Status == FileStatus.RightOnly, _ => true })
                && (query.Length == 0 || f.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase))).Select(f => f.RelativePath).ToArray();
        }
        var nodes = LeftFolders.SelectedItems.OfType<FolderNode>().ToArray();
        return _folderResult.Files.Where(f => nodes.Any(n => n.IsDirectory
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
        if (paths.Count == 0) { StatusLabel.Text = "没有可同步的文件；请选择文件或目录。"; return; }
        if (!await CanLeaveEdits()) return;
        _detailCancellation?.Cancel();
        _syncCancellation?.Dispose(); _syncCancellation = new();
        _syncActive = true; TextView.IsEnabled = false; UpdateEditState();
        _busy = true; CompareButton.IsEnabled = false; CancelButton.IsEnabled = true; ActivityLabel.Text = "生成同步预览…";
        try
        {
            var result = _folderResult;
            var progress = new Progress<SyncProgress>(p => StatusLabel.Text = $"校验 {p.Completed}/{p.Total} · {p.RelativePath}");
            var plan = await Task.Run(() => planner.PlanAsync(result.LeftPath, result.RightPath, paths, mode, progress, _syncCancellation.Token));
            _syncCancellation.Token.ThrowIfCancellationRequested();
            string scope = batch ? "批量范围：当前筛选匹配的全部文件（包括折叠目录中的文件）" : "选中范围：所选文件及目录内全部文件（目录内不受筛选限制）";
            var dialog = new SyncPreviewWindow(plan, preview, executor, scope, SafetyContext) { Owner = this };
            CancelButton.IsEnabled = false; ActivityLabel.Text = "预览中"; dialog.ShowDialog();
            if (dialog.Result is not null)
            {
                await RefreshAfterSync();
                StatusLabel.Text = $"同步完成：{dialog.Result.Entries.Count(r => r.Outcome == SyncOutcome.Copied)} 已复制，{dialog.Result.Entries.Count(r => r.Outcome == SyncOutcome.Failed)} 失败，{dialog.Result.Entries.Count(r => r.Outcome == SyncOutcome.Cancelled)} 取消。";
            }
        }
        catch (OperationCanceledException) { StatusLabel.Text = "已取消生成同步计划。"; }
        catch (Exception ex) { StatusLabel.Text = "同步失败：" + ex.Message; }
        finally { _syncActive = false; TextView.IsEnabled = true; UpdateEditState(); _busy = false; CompareButton.IsEnabled = _plugins.Diagnostics.Count == 0; CancelButton.IsEnabled = false; ActivityLabel.Text = "就绪"; }
    }
    private async Task RefreshAfterSync()
    {
        if (_rootRequest is null) return;
        ClearEditors();
        var expanded = _tree?.All.Where(n => n.IsDirectory).ToDictionary(n => n.RelativePath, n => n.IsExpanded);
        string? selected = (LeftFolders.SelectedItem as FolderNode)?.RelativePath;
        var result = await Task.Run(() => _engine.CompareAsync(_rootRequest, "directories"));
        _folderResult = result; _tree = await Task.Run(() => FolderTree.Build(result, CancellationToken.None));
        foreach (var node in _tree.All.Where(n => n.IsDirectory)) if (expanded?.TryGetValue(node.RelativePath, out bool value) == true) node.IsExpanded = value;
        _fileResult = null; _textFile = null; _currentNode = null; _presentation = null; _presentationVersion++;
        LeftCode.SetRows([]); RightCode.SetRows([]); Overview.SetPresentation(null); TextTab.IsEnabled = false;
        RefreshFolderRows(_tree.All.FirstOrDefault(n => n.RelativePath == selected)); SetView(false);
    }
}
