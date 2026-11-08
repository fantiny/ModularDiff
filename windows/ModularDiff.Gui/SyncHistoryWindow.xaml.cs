using System.Windows;
using System.Windows.Controls;
using ModularDiff.Contracts;

namespace ModularDiff.Gui;

public sealed record SyncOperationRow(SyncOperation Operation)
{
    public string Time => Operation.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public int Count => Operation.Entries.Count;
    public string State => Operation.State switch { "Running" => "未完成 / 待核对", "Completed" => "已完成", "Partial" => "部分失败", "Cancelled" => "已取消", "RecoveryPartial" => "部分恢复", "Restored" => "已恢复", _ => Operation.State };
}
public sealed class RecoveryRow(RecoveryItem item)
{
    public RecoveryItem Item => item;
    public bool CanRestore => item.CanRestore;
    public bool Selected { get; set; } = item.CanRestore;
}
public partial class SyncHistoryWindow : Window
{
    private readonly SyncSafetyContext _context;
    private readonly ISyncRecovery _recovery;
    private CancellationTokenSource? _cancellation;
    private int _version;
    private bool _running;
    public bool RecoveryAttempted { get; private set; }
    public IReadOnlyList<RecoveryRow> Rows { get; private set; } = [];
    public SyncHistoryWindow(SyncSafetyContext context, ISyncRecovery recovery)
    {
        InitializeComponent(); _context = context; _recovery = recovery;
        StoreLabel.Text = context.StoreRoot; StoreLabel.ToolTip = "备份与 JSON 操作记录保存在此目录；重启后仍可恢复\n" + context.StoreRoot;
        Loaded += async (_, _) => await ReloadAsync();
        Closing += (_, e) => { if (_running) { e.Cancel = true; _cancellation?.Cancel(); } };
        Closed += (_, _) => { _version++; _cancellation?.Cancel(); };
    }
    public async Task ReloadAsync(string? selectedId = null)
    {
        try
        {
            var listing = await _context.Journal.ListAsync(_context.StoreRoot);
            var rows = listing.Operations.Select(o => new SyncOperationRow(o)).ToArray();
            OperationsGrid.ItemsSource = rows;
            Status.Text = listing.Errors.Count == 0 ? $"{rows.Length} 条操作记录" : $"{rows.Length} 条操作记录，{listing.Errors.Count} 条损坏或不可读";
            Status.ToolTip = string.Join("\n", listing.Errors);
            OperationsGrid.SelectedItem = rows.FirstOrDefault(r => r.Operation.Id == selectedId) ?? rows.FirstOrDefault();
        }
        catch (Exception ex) { Status.Text = "无法读取历史：" + ex.Message; }
    }
    private async void OperationSelected(object sender, SelectionChangedEventArgs e) => await PreviewSelectedAsync();
    public async Task PreviewSelectedAsync()
    {
        int version = ++_version; RestoreButton.IsEnabled = false; Rows = []; EntriesGrid.ItemsSource = Rows;
        if (OperationsGrid.SelectedItem is not SyncOperationRow selected) return;
        Summary.Text = "校验备份和当前文件…";
        try
        {
            var plan = await Task.Run(() => _recovery.PreviewAsync(_context, selected.Operation.Id));
            if (version != _version) return;
            Rows = plan.Items.Select(i => new RecoveryRow(i)).ToArray(); EntriesGrid.ItemsSource = Rows;
            Summary.Text = $"{Rows.Count} 项 · {Rows.Count(r => r.CanRestore)} 可恢复 · {Rows.Count(r => !r.CanRestore)} 跳过";
            RestoreButton.IsEnabled = Rows.Any(r => r.CanRestore) && !_running;
        }
        catch (Exception ex) { if (version == _version) Summary.Text = "恢复预览失败：" + ex.Message; }
    }
    private async void RestoreClick(object sender, RoutedEventArgs e) => await RestoreSelectedAsync();
    public async Task RestoreSelectedAsync()
    {
        if (_running || OperationsGrid.SelectedItem is not SyncOperationRow selected) return;
        var ids = Rows.Where(r => r.Selected && r.CanRestore).Select(r => r.Item.EntryId).ToArray(); if (ids.Length == 0) return;
        _running = true; RecoveryAttempted = true; _version++; _cancellation?.Dispose(); _cancellation = new();
        OperationsGrid.IsEnabled = EntriesGrid.IsEnabled = RefreshButton.IsEnabled = RestoreButton.IsEnabled = false; StopButton.IsEnabled = true;
        try
        {
            var result = await Task.Run(() => _recovery.RestoreAsync(_context, selected.Operation.Id, ids, cancellationToken: _cancellation.Token));
            Status.Text = $"已恢复 {result.Restored} · 冲突 {result.Conflicts} · 失败 {result.Failed}" + (result.Cancelled ? " · 已取消" : "");
        }
        catch (Exception ex) { Status.Text = "恢复未完成；重新读取历史核对：" + ex.Message; }
        finally
        {
            _running = false; OperationsGrid.IsEnabled = EntriesGrid.IsEnabled = RefreshButton.IsEnabled = true; StopButton.IsEnabled = false;
            string status = Status.Text; await ReloadAsync(selected.Operation.Id); Status.Text = status;
        }
    }
    private async void RefreshClick(object sender, RoutedEventArgs e) => await ReloadAsync();
    private void StopClick(object sender, RoutedEventArgs e) => _cancellation?.Cancel();
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
