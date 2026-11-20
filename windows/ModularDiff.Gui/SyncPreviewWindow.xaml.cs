using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ModularDiff.Contracts;
using ModularDiff.Gui.Controls;

namespace ModularDiff.Gui;

public sealed class SyncPreviewRow(SyncPreviewItem item) : INotifyPropertyChanged
{
    public string RelativePath => item.RelativePath;
    public string LeftInfo => LocalizeInfo(item.LeftInfo);
    public string RightInfo => LocalizeInfo(item.RightInfo);
    public IReadOnlyList<SyncActionChoice> Choices { get; } =
        item.Choices.Select(c => new SyncActionChoice(c.Action, Lang.Get(c.Label))).ToArray();
    private SyncAction _action = item.SuggestedAction;
    private string _state = LocalizeState(item.State);
    public bool CanChoose { get; private set; } = true;
    public void Freeze() { CanChoose = false; Changed(nameof(CanChoose)); }
    public SyncAction Action { get => _action; set { if (!Choices.Any(c => c.Action == value)) return; _action = value; Changed(nameof(Action)); } }
    public string State { get => _state; set { _state = value; Changed(nameof(State)); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
    private static string LocalizeInfo(string info) =>
        info is "sync_stamp_missing" or "不存在" ? Lang.Get("sync_stamp_missing") : info;
    private static string LocalizeState(string state) =>
        state.StartsWith("sync_", StringComparison.Ordinal) || state.StartsWith("file_status_", StringComparison.Ordinal)
            ? Lang.Get(state) : state;
}

public partial class SyncPreviewWindow : Window
{
    private readonly SyncPlan _plan;
    private readonly ISyncExecutor _executor;
    private readonly SyncSafetyContext? _safety;
    private readonly Dictionary<string, SyncItem> _items;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _running, _executed;
    public IReadOnlyList<SyncPreviewRow> Rows { get; }
    public SyncResult? Result { get; private set; }
    public SyncPreviewWindow(SyncPlan plan, ISyncPreview preview, ISyncExecutor executor, string scope, SyncSafetyContext? safety = null)
    {
        InitializeComponent(); _safety = safety; _plan = plan; _executor = executor; _items = plan.Items.ToDictionary(i => i.RelativePath);
        Rows = preview.Describe(plan).Select(i => new SyncPreviewRow(i)).ToArray(); ItemsGrid.ItemsSource = Rows;
        foreach (var row in Rows) row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SyncPreviewRow.Action)) UpdateSummary(); };
        LocalizeChrome();
        Roots.Text = Lang.Get("sync_roots", plan.LeftRoot, plan.RightRoot); Roots.ToolTip = Roots.Text;
        Scope.Text = scope; Scope.ToolTip = scope + "\n" + Lang.Get("sync_scope_tip_suffix");
        Closing += (_, e) => { if (_running) { e.Cancel = true; _cancellation.Cancel(); } };
        Closed += (_, _) => _cancellation.Dispose(); UpdateSummary();
    }
    private void LocalizeChrome()
    {
        Title = Lang.Get("sync_preview_title");
        ExecuteButton.ToolTip = Lang.Get("sync_preview_execute_tip");
        StopButton.ToolTip = Lang.Get("sync_preview_stop_tip");
        ClosePreviewButton.ToolTip = Lang.Get("sync_preview_close_tip");
        if (ItemsGrid.Columns.Count >= 5)
        {
            ItemsGrid.Columns[0].Header = Lang.Get("sync_col_action");
            ItemsGrid.Columns[1].Header = Lang.Get("sync_col_relpath");
            ItemsGrid.Columns[2].Header = Lang.Get("sync_col_left");
            ItemsGrid.Columns[3].Header = Lang.Get("sync_col_right");
            ItemsGrid.Columns[4].Header = Lang.Get("sync_col_state");
        }
    }
    private void UpdateSummary()
    {
        int copies = Rows.Count(r => r.Action != SyncAction.Skip);
        int overwrite = Rows.Count(r => r.Action != SyncAction.Skip && (r.Action == SyncAction.LeftToRight ? _items[r.RelativePath].Right.Exists : _items[r.RelativePath].Left.Exists));
        Summary.Text = Lang.Get("sync_summary", Rows.Count, copies - overwrite, overwrite, Rows.Count - copies);
        ExecuteButton.IsEnabled = copies > 0 && !_running && !_executed;
    }
    private async void ExecuteClick(object sender, RoutedEventArgs e) => await ExecuteAsync();
    public async Task ExecuteAsync()
    {
        if (_running || _executed || !Rows.Any(r => r.Action != SyncAction.Skip)) return;
        _running = _executed = true; ItemsGrid.IsEnabled = false; ExecuteButton.IsEnabled = false; StopButton.IsEnabled = true;
        Progress.Maximum = Math.Max(1, Rows.Count);
        foreach (var row in Rows) row.Freeze();
        var progress = new Progress<SyncProgress>(p => { Progress.Value = p.Completed; });
        var decisions = Rows.Select(r => new SyncDecision(r.RelativePath, r.Action)).ToArray();
        try
        {
            Result = await Task.Run(() => _safety is null ? _executor.ExecuteAsync(_plan, decisions, progress, _cancellation.Token) : _executor.ExecuteAsync(_plan, decisions, _safety, progress, _cancellation.Token));
            Scope.Text = Lang.Get("sync_journal_ready", Result.OperationId ?? "");
            var rowsByPath = Rows.ToDictionary(r => r.RelativePath);
            foreach (var result in Result.Entries) if (rowsByPath.TryGetValue(result.RelativePath, out var row)) row.State = result.Message;
            Summary.ToolTip = string.Join("\n", Result.Entries.Where(r => r.Outcome == SyncOutcome.Failed || r.Message.Contains("记录写入失败")).Select(r => r.RelativePath + "：" + r.Message));
            Summary.Text = Lang.Get("sync_result_summary",
                Result.Entries.Count(r => r.Outcome == SyncOutcome.Copied),
                Result.Entries.Count(r => r.Outcome == SyncOutcome.Failed),
                Result.Entries.Count(r => r.Outcome == SyncOutcome.Skipped),
                Result.Entries.Count(r => r.Outcome == SyncOutcome.Cancelled));
        }
        catch (Exception ex) { Summary.Text = Lang.Get("sync_incomplete", ex.Message); Result = new([], _cancellation.IsCancellationRequested); }
        finally { _running = false; StopButton.IsEnabled = false; ItemsGrid.IsEnabled = true; ItemsGrid.IsReadOnly = true; }
    }
    private void StopClick(object sender, RoutedEventArgs e) => _cancellation.Cancel();
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void PreviewKey(object sender, KeyEventArgs e) { if (e.Key != Key.Escape) return; e.Handled = true; if (_running) _cancellation.Cancel(); else Close(); }
}
