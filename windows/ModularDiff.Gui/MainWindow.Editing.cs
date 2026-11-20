using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using ModularDiff.Contracts;
using ModularDiff.Core;
using ModularDiff.Gui.Controls;

namespace ModularDiff.Gui;

public partial class MainWindow
{
    /// <summary>Single source of truth for the active comparison's edit state.
    /// The forwarding properties below keep existing code working while all
    /// mutations route through the session (single-writer principle).</summary>
    private ComparisonSession _session = null!;
    private IEditBuffer? _leftBuffer { get => _session?.Left; set { if (value is not null && _session is not null) _session.Attach(value, _session.Right ?? value); else _session?.Clear(); } }
    private IEditBuffer? _rightBuffer { get => _session?.Right; set { if (value is not null && _session is not null) _session.Attach(_session.Left ?? value, value); else _session?.Clear(); } }
    private BufferComparison? _bufferDiff { get => _session?.Comparison; set => _session?.SetComparison(value); }
    private TextPresentation? _presentation { get => _session?.Presentation; set => _session?.SetPresentation(value); }
    private long _presentationVersion { get => _session?.Version ?? 0; set { /* version managed by session mutations */ } }
    private DiffPane? _activePane;
    private CancellationTokenSource? _editCancellation;
    private Task _editTask = Task.CompletedTask;
    private bool _saving, _allowClose, _closingPrompt;
    private bool HasDirtyBuffers => _leftBuffer?.IsDirty == true || _rightBuffer?.IsDirty == true;

    private void InitializeEditing()
    {
        BufferBox.ItemsSource = _plugins.GetAll<IEditBufferFactory>(); BufferBox.SelectedIndex = 0;
        StorageBox.ItemsSource = _plugins.GetAll<ITextStorage>(); StorageBox.SelectedIndex = 0;
        BufferCompareBox.ItemsSource = _plugins.GetAll<IBufferComparison>(); BufferCompareBox.SelectedIndex = 0;
        MergeBox.ItemsSource = _plugins.GetAll<IBlockMerge>(); MergeBox.SelectedIndex = 0;
        LeftCode.EditRequested += EditRequested; RightCode.EditRequested += EditRequested;
        LeftCode.HistoryRequested += HistoryRequested; RightCode.HistoryRequested += HistoryRequested;
        LeftCode.GotKeyboardFocus += (_, _) => { _activePane = LeftCode; UpdateEditState(); };
        RightCode.GotKeyboardFocus += (_, _) => { _activePane = RightCode; UpdateEditState(); };
        Closing += EditorClosing;
        Closed += (_, _) => _editCancellation?.Cancel();
        UpdateEditState();
    }
    private void ClearEditors()
    {
        _editCancellation?.Cancel(); _bufferDiff = null;
        _leftBuffer = _rightBuffer = null; LeftCode.AttachBuffer(null); RightCode.AttachBuffer(null);
        UpdateEditState();
        if (FileStatsLabel is not null) UpdateFileStats();
    }
    private async Task AttachEditors(string left, string right, CancellationToken token)
    {
        if (_textFile is null || _textFile.IsBinary || _textFile.Status == FileStatus.Error
            || StorageBox.SelectedItem is not ITextStorage storage || BufferBox.SelectedItem is not IEditBufferFactory factory
            || BufferCompareBox.SelectedItem is not IBufferComparison) return;
        try
        {
            var snapshots = await Task.WhenAll(storage.LoadAsync(left, true, token), storage.LoadAsync(right, true, token));
            token.ThrowIfCancellationRequested();
            _leftBuffer = factory.Create(snapshots[0]); _rightBuffer = factory.Create(snapshots[1]);
            LeftCode.AttachBuffer(_leftBuffer); RightCode.AttachBuffer(_rightBuffer); _activePane = LeftCode;
            _editTask = RecompareBuffers(false); await _editTask;
            UpdateFileStats();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { ClearEditors(); StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_readonly_keep", ex.Message); }
        UpdateEditState();
    }
    private void EditRequested(DiffPane pane, TextEdit edit)
    {
        if (_syncActive || pane.Buffer is null) return;
        try
        {
            pane.Buffer.Apply(edit); _activePane = pane;
            ShowDraft(); UpdateEditState();
            if (_autoRealign) _editTask = RecompareBuffers(true);
            else HunkLabel.Text = ModularDiff.Gui.Controls.Lang.Get("hunks_unaligned");
        }
        catch (Exception ex) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_edit_failed", ex.Message); }
    }
    private void ShowDraft()
    {
        if (_leftBuffer is null || _rightBuffer is null) return;
        _bufferDiff = null; _presentation = null; _currentHunk = -1;
        var left = DocumentLines.Parse(_leftBuffer.Text); var right = DocumentLines.Parse(_rightBuffer.Text);
        var rows = new List<AlignedRow>();
        for (int i = 0; i < Math.Max(left.Count, right.Count); i++)
        {
            TextCell? a = i < left.Count ? new(i + 1, left[i].Text, left[i].EndingLabel, []) : null;
            TextCell? b = i < right.Count ? new(i + 1, right[i].Text, right[i].EndingLabel, []) : null;
            rows.Add(new(i, a, b));
        }
        _syncText = true; LeftCode.SetRows(rows, false); RightCode.SetRows(rows, false); _syncText = false;
        TextEmptyState.Visibility = Visibility.Collapsed; HunkLabel.Text = ModularDiff.Gui.Controls.Lang.Get("activity_recomparing"); ActivityLabel.Text = ModularDiff.Gui.Controls.Lang.Get("activity_buffer_updated");
        Overview.SetPresentation(null); ExportButton.IsEnabled = false;
    }
    private async Task RecompareBuffers(bool debounce)
    {
        if (_leftBuffer is null || _rightBuffer is null || _rootRequest is null || BufferCompareBox.SelectedItem is not IBufferComparison comparer) return;
        _editCancellation?.Cancel(); _editCancellation?.Dispose();
        var cancellation = new CancellationTokenSource(); _editCancellation = cancellation; var token = cancellation.Token;
        var leftBuffer = _leftBuffer; var rightBuffer = _rightBuffer;
        var left = leftBuffer.Snapshot(); var right = rightBuffer.Snapshot(); var request = _rootRequest;
        try
        {
            if (debounce) await Task.Delay(100, token);
            var result = await Task.Run(() => comparer.Compare(left, right, request.AlgorithmId, request.EffectiveOptions, _plugins, token), token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_leftBuffer, leftBuffer) || !ReferenceEquals(_rightBuffer, rightBuffer) || leftBuffer.Version != left.Version || rightBuffer.Version != right.Version) return;
            _bufferDiff = result;
            _textFile = result.File with { RelativePath = _currentNode?.RelativePath ?? result.File.RelativePath };
            _fileResult = new(left.Path, right.Path, "files", request.AlgorithmId, [_textFile], []);
            await RebuildPresentation(token, preservePosition: true);
            token.ThrowIfCancellationRequested();
            var pane = _activePane ?? LeftCode;
            pane.SetCaret(pane.CaretOffset, extend: true);
            int row = pane.SelectedRow;
            if (_presentation is not null && row >= 0 && row < _presentation.Rows.Count)
            {
                _currentHunk = _presentation.Rows[row].HunkId;
                if (_currentHunk >= 0) HunkLabel.Text = ModularDiff.Gui.Controls.Lang.Get("hunks_index", _currentHunk + 1, _presentation.Hunks.Count);
            }
            ExportButton.IsEnabled = true; ActivityLabel.Text = ModularDiff.Gui.Controls.Lang.Get("activity_ready"); UpdateEditState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) { _bufferDiff = null; UpdateEditState(); StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_recompare_failed", ex.Message); }
        }
    }
    private void UpdateEditState()
    {
        if (SaveLeftButton is null) return;
        bool text = TextView.Visibility == Visibility.Visible && !_syncActive;
        bool validDiff = _bufferDiff is not null && _leftBuffer?.Version == _bufferDiff.LeftVersion && _rightBuffer?.Version == _bufferDiff.RightVersion;
        // Path-bar Save Left/Right sit next to the address boxes; disable on the folder tab
        // so directory roots are not paired with file-buffer save actions.
        SaveLeftButton.IsEnabled = text && _leftBuffer is not null && !_saving && !_syncActive;
        SaveRightButton.IsEnabled = text && _rightBuffer is not null && !_saving && !_syncActive;
        SaveAllButton.IsEnabled = text && (_leftBuffer is not null || _rightBuffer is not null) && !_saving && !_syncActive;
        UndoButton.IsEnabled = text && _activePane?.Buffer?.CanUndo == true;
        RedoButton.IsEnabled = text && _activePane?.Buffer?.CanRedo == true;
        MergeRightButton.IsEnabled = MergeLeftButton.IsEnabled = text && validDiff && _currentHunk >= 0 && MergeBox.SelectedItem is IBlockMerge;
        CopyAllRightButton.IsEnabled = CopyAllLeftButton.IsEnabled = text && validDiff && _presentation is { Hunks.Count: > 0 } && MergeBox.SelectedItem is IBlockMerge;
        if (!text)
        {
            bool dirtyTabs = _textSessions.Any(s => s.IsDirty || (ReferenceEquals(s, _activeSession) && HasDirtyBuffers));
            EditStatusLabel.Text = dirtyTabs ? ModularDiff.Gui.Controls.Lang.Get("edit_status_dirty_tabs") : ModularDiff.Gui.Controls.Lang.Get("edit_status_folder");
            EditStatusLabel.ToolTip = dirtyTabs ? ModularDiff.Gui.Controls.Lang.Get("edit_status_dirty_tabs_tip") : ModularDiff.Gui.Controls.Lang.Get("edit_status_folder_tip");
            return;
        }
        EditStatusLabel.Text = _leftBuffer is null ? ModularDiff.Gui.Controls.Lang.Get("edit_status_readonly") : HasDirtyBuffers ? ModularDiff.Gui.Controls.Lang.Get("edit_status_dirty") : ModularDiff.Gui.Controls.Lang.Get("edit_status_saved");
        EditStatusLabel.ToolTip = _leftBuffer is null ? ModularDiff.Gui.Controls.Lang.Get("readonly_tooltip") : ModularDiff.Gui.Controls.Lang.Get("dirty_tooltip");
        if (_leftBuffer is not null && _rightBuffer is not null)
        {
            LeftFileTitle.Text = (_leftBuffer.IsDirty ? "● " : "") + _leftBuffer.Path;
            RightFileTitle.Text = (_rightBuffer.IsDirty ? "● " : "") + _rightBuffer.Path;
            LeftFileTitle.ToolTip = $"{_leftBuffer.Path}\n{_leftBuffer.Snapshot().Encoding.Name} · {(_leftBuffer.IsDirty ? ModularDiff.Gui.Controls.Lang.Get("unsaved_label") : ModularDiff.Gui.Controls.Lang.Get("saved_label"))}";
            RightFileTitle.ToolTip = $"{_rightBuffer.Path}\n{_rightBuffer.Snapshot().Encoding.Name} · {(_rightBuffer.IsDirty ? ModularDiff.Gui.Controls.Lang.Get("unsaved_label") : ModularDiff.Gui.Controls.Lang.Get("saved_label"))}";
            if (_activeSession is not null) _activeSession.IsDirty = HasDirtyBuffers;
        }
    }
    private async void CopyLeftToRight(object sender, RoutedEventArgs e) => await CopyBlock(true);
    private async void CopyRightToLeft(object sender, RoutedEventArgs e) => await CopyBlock(false);
    private async void CopyAllRight(object sender, RoutedEventArgs e) => await CopyAllBlocks(true);
    private async void CopyAllLeft(object sender, RoutedEventArgs e) => await CopyAllBlocks(false);

    /// <summary>Copies every difference block to the other side in one pass — like Beyond
    /// Compare's "Copy All Left→Right". Plans all edits from one consistent snapshot, then
    /// applies them bottom-up so lower offsets are never shifted by higher edits.</summary>
    private async Task CopyAllBlocks(bool toRight)
    {
        if (_syncActive || _bufferDiff is null || _leftBuffer is null || _rightBuffer is null
            || _presentation is null || _presentation.Hunks.Count == 0
            || MergeBox.SelectedItem is not IBlockMerge merge) return;
        int copied = 0;
        try
        {
            var leftSnapshot = _leftBuffer.Snapshot();
            var rightSnapshot = _rightBuffer.Snapshot();
            var edits = new List<TextEdit>();
            for (int hunk = _presentation.Hunks.Count - 1; hunk >= 0; hunk--)
                edits.Add(merge.Plan(_bufferDiff, leftSnapshot, rightSnapshot, hunk,
                    toRight ? MergeDirection.LeftToRight : MergeDirection.RightToLeft));
            var destination = toRight ? RightCode : LeftCode;
            foreach (var edit in edits) destination.Buffer!.Apply(edit);
            copied = edits.Count;
            _activePane = destination;
            ShowDraft(); UpdateEditState();
            _editTask = RecompareBuffers(false);
            await _editTask;
            StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_copied_hunks", copied, ModularDiff.Gui.Controls.Lang.Get(toRight ? "side_right" : "side_left"));
        }
        catch (Exception ex) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_copy_all_failed", copied, ex.Message); }
    }
    private async Task CopyBlock(bool toRight)
    {
        if (_syncActive || _bufferDiff is null || _leftBuffer is null || _rightBuffer is null || MergeBox.SelectedItem is not IBlockMerge merge || _currentHunk < 0) return;
        try
        {
            var edit = merge.Plan(_bufferDiff, _leftBuffer.Snapshot(), _rightBuffer.Snapshot(), _currentHunk, toRight ? MergeDirection.LeftToRight : MergeDirection.RightToLeft);
            var pane = toRight ? RightCode : LeftCode;
            EditRequested(pane, edit); pane.SetCaret(edit.Offset + edit.Text.Length); pane.Focus();
            await _editTask;
            NavigateHunk(1);   // like Beyond Compare: after a block copy, advance to the next difference
        }
        catch (Exception ex) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_copy_block_failed", ex.Message); }
    }
    private void HistoryRequested(DiffPane pane, bool redo)
    {
        if (_syncActive || pane.Buffer is null || (redo ? !pane.Buffer.CanRedo : !pane.Buffer.CanUndo)) return;
        _activePane = pane;
        int caret = redo ? pane.Buffer.Redo() : pane.Buffer.Undo();
        ShowDraft(); pane.SetCaret(caret); pane.Focus(); UpdateEditState();
        if (_autoRealign) _editTask = RecompareBuffers(false);
    }
    private void UndoClick(object sender, RoutedEventArgs e) { if (_activePane is not null) HistoryRequested(_activePane, false); }
    private void RedoClick(object sender, RoutedEventArgs e) { if (_activePane is not null) HistoryRequested(_activePane, true); }
    private async void SaveLeft(object sender, RoutedEventArgs e) => await SaveBuffers([_leftBuffer]);
    private async void SaveRight(object sender, RoutedEventArgs e) => await SaveBuffers([_rightBuffer]);
    private async void SaveAll(object sender, RoutedEventArgs e) => await SaveBuffers([_leftBuffer, _rightBuffer]);
    private async Task<bool> SaveBuffers(IEnumerable<IEditBuffer?> buffers)
    {
        if (_syncActive || _saving || StorageBox.SelectedItem is not ITextStorage storage) return false;
        _saving = true; UpdateEditState(); int count = 0;
        try
        {
            foreach (var buffer in buffers.OfType<IEditBuffer>().Distinct())
            {
                var snapshot = buffer.Snapshot();
                if (!buffer.IsDirty && snapshot.Disk.Exists) continue;
                var disk = await storage.SaveAsync(snapshot);
                buffer.AcceptSave(snapshot, disk); count++;
            }
            if (_folderResult is not null && count > 0) FolderSummary.ToolTip = ModularDiff.Gui.Controls.Lang.Get("folder_saved_tip");
            _editTask = RecompareBuffers(false); await _editTask;
            StatusLabel.Text = count == 0 ? ModularDiff.Gui.Controls.Lang.Get("status_nothing_to_save") : ModularDiff.Gui.Controls.Lang.Get("status_saved_count", count);
            return true;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_saved_partial", count, ex.Message);
            if (!_arguments.Contains("--edit-smoke-test")) MessageBox.Show(this, StatusLabel.Text, ModularDiff.Gui.Controls.Lang.Get("dlg_save_failed_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { _saving = false; UpdateEditState(); }
    }
    private async Task<bool> CanLeaveEdits()
    {
        if (_saving) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_saving_wait"); return false; }
        if (!HasDirtyBuffers && !Hex.IsDirty) return true;
        var choice = MessageBox.Show(this, Hex.IsDirty && !HasDirtyBuffers
                ? ModularDiff.Gui.Controls.Lang.Get("dlg_unsaved_hex")
                : ModularDiff.Gui.Controls.Lang.Get("dlg_unsaved_text"),
            ModularDiff.Gui.Controls.Lang.Get("dlg_unsaved_title"), MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (choice == MessageBoxResult.No) return true;
        if (choice != MessageBoxResult.Yes) return false;
        // 文本与十六进制编辑一并保存 —— 此前 hex 修改不在任何离开/关闭提示内,静默丢弃(审查发现)。
        bool saved = await SaveBuffers([_leftBuffer, _rightBuffer]) && !HasDirtyBuffers;
        if (Hex.IsDirty) saved = Hex.Save() && saved;
        return saved;
    }
    private async void EditorClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        if (!HasDirtyBuffers && !Hex.IsDirty && !_saving) return;
        e.Cancel = true;
        if (_closingPrompt) return;
        _closingPrompt = true;
        try { if (await CanLeaveEdits()) { _allowClose = true; Close(); } }
        finally { _closingPrompt = false; }
    }
    private async Task<bool> EditingShortcut(KeyEventArgs e)
    {
        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        if (control && alt && (e.Key is Key.Left or Key.Right || e.SystemKey is Key.Left or Key.Right))
        { e.Handled = true; await CopyBlock(e.Key == Key.Right || e.SystemKey == Key.Right); return true; }
        if (control && e.Key == Key.S && _leftBuffer is not null)
        { e.Handled = true; await SaveBuffers(shift ? [_leftBuffer, _rightBuffer] : [_activePane?.Buffer]); return true; }
        if (control && e.Key is Key.Z or Key.Y && (_activePane?.IsKeyboardFocusWithin == true))
        { e.Handled = true; HistoryRequested(_activePane, e.Key == Key.Y || shift); return true; }
        return false;
    }
}
