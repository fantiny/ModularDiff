using System.Windows;
using System.Windows.Controls;
using ModularDiff.Contracts;
using ModularDiff.Gui.Controls;

namespace ModularDiff.Gui;

public sealed record SyncOperationRow(SyncOperation Operation)
{
    public string Time => Operation.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public int Count => Operation.Entries.Count;
    public string State => Operation.State switch
    {
        "Running" => Lang.Get("sync_op_running"),
        "Completed" => Lang.Get("sync_op_completed"),
        "Partial" => Lang.Get("sync_op_partial"),
        "Cancelled" => Lang.Get("sync_op_cancelled"),
        "RecoveryPartial" => Lang.Get("sync_op_recovery_partial"),
        "Restored" => Lang.Get("sync_op_restored"),
        _ => Operation.State,
    };
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
        LocalizeChrome();
        StoreLabel.Text = context.StoreRoot; StoreLabel.ToolTip = Lang.Get("sync_hist_store_tip", context.StoreRoot);
        Loaded += async (_, _) => await ReloadAsync();
        Closing += (_, e) => { if (_running) { e.Cancel = true; _cancellation?.Cancel(); } };
        Closed += (_, _) => { _version++; _cancellation?.Cancel(); };
    }
    private void LocalizeChrome()
    {
        Title = Lang.Get("sync_history_title");
        RestoreButton.ToolTip = Lang.Get("sync_hist_restore_tip");
        RefreshButton.ToolTip = Lang.Get("sync_hist_refresh_tip");
        StopButton.ToolTip = Lang.Get("sync_hist_stop_tip");
        CloseHistoryButton.ToolTip = Lang.Get("sync_hist_close_tip");
        if (OperationsGrid.Columns.Count >= 5)
        {
            OperationsGrid.Columns[0].Header = Lang.Get("sync_col_time");
            OperationsGrid.Columns[1].Header = Lang.Get("col_status");
            OperationsGrid.Columns[2].Header = Lang.Get("sync_col_files");
            OperationsGrid.Columns[3].Header = Lang.Get("sync_col_left_root");
            OperationsGrid.Columns[4].Header = Lang.Get("sync_col_right_root");
        }
        if (EntriesGrid.Columns.Count >= 4)
        {
            EntriesGrid.Columns[0].Header = Lang.Get("sync_col_restore");
            EntriesGrid.Columns[1].Header = Lang.Get("sync_col_relpath");
            EntriesGrid.Columns[2].Header = Lang.Get("sync_col_restore_action");
            EntriesGrid.Columns[3].Header = Lang.Get("sync_col_conflict_reason");
        }
    }
    public async Task ReloadAsync(string? selectedId = null)
    {
        try
        {
            var listing = await _context.Journal.ListAsync(_context.StoreRoot);
            var rows = listing.Operations.Select(o => new SyncOperationRow(o)).ToArray();
            OperationsGrid.ItemsSource = rows;
            Status.Text = listing.Errors.Count == 0
                ? Lang.Get("sync_hist_count", rows.Length)
                : Lang.Get("sync_hist_count_errors", rows.Length, listing.Errors.Count);
            Status.ToolTip = string.Join("\n", listing.Errors);
            OperationsGrid.SelectedItem = rows.FirstOrDefault(r => r.Operation.Id == selectedId) ?? rows.FirstOrDefault();
        }
        catch (Exception ex) { Status.Text = Lang.Get("sync_hist_read_failed", ex.Message); }
    }
    private async void OperationSelected(object sender, SelectionChangedEventArgs e) => await PreviewSelectedAsync();
    public async Task PreviewSelectedAsync()
    {
        int version = ++_version; RestoreButton.IsEnabled = false; Rows = []; EntriesGrid.ItemsSource = Rows;
        if (OperationsGrid.SelectedItem is not SyncOperationRow selected) return;
        Summary.Text = Lang.Get("sync_hist_validating");
        try
        {
            var plan = await Task.Run(() => _recovery.PreviewAsync(_context, selected.Operation.Id));
            if (version != _version) return;
            Rows = plan.Items.Select(i => new RecoveryRow(i)).ToArray(); EntriesGrid.ItemsSource = Rows;
            Summary.Text = Lang.Get("sync_hist_preview_summary", Rows.Count, Rows.Count(r => r.CanRestore), Rows.Count(r => !r.CanRestore));
            RestoreButton.IsEnabled = Rows.Any(r => r.CanRestore) && !_running;
        }
        catch (Exception ex) { if (version == _version) Summary.Text = Lang.Get("sync_hist_preview_failed", ex.Message); }
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
            Status.Text = Lang.Get("sync_hist_restored", result.Restored, result.Conflicts, result.Failed)
                + (result.Cancelled ? Lang.Get("sync_hist_cancelled_suffix") : "");
        }
        catch (Exception ex) { Status.Text = Lang.Get("sync_hist_restore_incomplete", ex.Message); }
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
