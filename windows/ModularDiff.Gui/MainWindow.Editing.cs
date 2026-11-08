using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using ModularDiff.Contracts;
using ModularDiff.Gui.Controls;

namespace ModularDiff.Gui;

public partial class MainWindow
{
    private IEditBuffer? _leftBuffer, _rightBuffer;
    private BufferComparison? _bufferDiff;
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
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { ClearEditors(); StatusLabel.Text = "文件保持只读：" + ex.Message; }
        UpdateEditState();
    }
    private void EditRequested(DiffPane pane, TextEdit edit)
    {
        if (_syncActive || pane.Buffer is null) return;
        try
        {
            pane.Buffer.Apply(edit); _activePane = pane;
            ShowDraft(); UpdateEditState();
            _editTask = RecompareBuffers(true);
        }
        catch (Exception ex) { StatusLabel.Text = "编辑失败：" + ex.Message; }
    }
    private void ShowDraft()
    {
        if (_leftBuffer is null || _rightBuffer is null) return;
        _bufferDiff = null; _presentationVersion++; _presentation = null; _currentHunk = -1;
        var left = DocumentLines.Parse(_leftBuffer.Text); var right = DocumentLines.Parse(_rightBuffer.Text);
        var rows = new List<AlignedRow>();
        for (int i = 0; i < Math.Max(left.Count, right.Count); i++)
        {
            TextCell? a = i < left.Count ? new(i + 1, left[i].Text, left[i].EndingLabel, []) : null;
            TextCell? b = i < right.Count ? new(i + 1, right[i].Text, right[i].EndingLabel, []) : null;
            rows.Add(new(i, a, b));
        }
        _syncText = true; LeftCode.SetRows(rows, false); RightCode.SetRows(rows, false); _syncText = false;
        TextEmptyState.Visibility = Visibility.Collapsed; HunkLabel.Text = "重比较…"; ActivityLabel.Text = "缓冲已更新";
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
                if (_currentHunk >= 0) HunkLabel.Text = $"差异 {_currentHunk + 1} / {_presentation.Hunks.Count}";
            }
            ExportButton.IsEnabled = true; ActivityLabel.Text = "就绪"; UpdateEditState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) { _bufferDiff = null; UpdateEditState(); StatusLabel.Text = "重比较失败，编辑已保留：" + ex.Message; }
        }
    }
    private void UpdateEditState()
    {
        if (SaveLeftButton is null) return;
        bool text = TextView.Visibility == Visibility.Visible && !_syncActive;
        bool validDiff = _bufferDiff is not null && _leftBuffer?.Version == _bufferDiff.LeftVersion && _rightBuffer?.Version == _bufferDiff.RightVersion;
        SaveLeftButton.IsEnabled = _leftBuffer is not null && !_saving && !_syncActive;
        SaveRightButton.IsEnabled = _rightBuffer is not null && !_saving && !_syncActive;
        SaveAllButton.IsEnabled = (_leftBuffer is not null || _rightBuffer is not null) && !_saving && !_syncActive;
        UndoButton.IsEnabled = text && _activePane?.Buffer?.CanUndo == true;
        RedoButton.IsEnabled = text && _activePane?.Buffer?.CanRedo == true;
        MergeRightButton.IsEnabled = MergeLeftButton.IsEnabled = text && validDiff && _currentHunk >= 0 && MergeBox.SelectedItem is IBlockMerge;
        EditStatusLabel.Text = _leftBuffer is null ? "只读" : HasDirtyBuffers ? "● 未保存" : "已保存";
        EditStatusLabel.ToolTip = _leftBuffer is null ? "二进制、大文件或没有编辑插件时使用只读视图" : "修改先保存在内存中；点击保存图标写入磁盘";
        if (_leftBuffer is not null && _rightBuffer is not null)
        {
            LeftFileTitle.Text = (_leftBuffer.IsDirty ? "● " : "") + _leftBuffer.Path;
            RightFileTitle.Text = (_rightBuffer.IsDirty ? "● " : "") + _rightBuffer.Path;
            LeftFileTitle.ToolTip = $"{_leftBuffer.Path}\n{_leftBuffer.Snapshot().Encoding.Name} · {(_leftBuffer.IsDirty ? "未保存" : "已保存")}";
            RightFileTitle.ToolTip = $"{_rightBuffer.Path}\n{_rightBuffer.Snapshot().Encoding.Name} · {(_rightBuffer.IsDirty ? "未保存" : "已保存")}";
            TextTab.Content = (HasDirtyBuffers ? "● " : "≡ ") + System.IO.Path.GetFileName(_leftBuffer.Path);
        }
    }
    private async void CopyLeftToRight(object sender, RoutedEventArgs e) => await CopyBlock(true);
    private async void CopyRightToLeft(object sender, RoutedEventArgs e) => await CopyBlock(false);
    private async Task CopyBlock(bool toRight)
    {
        if (_syncActive || _bufferDiff is null || _leftBuffer is null || _rightBuffer is null || MergeBox.SelectedItem is not IBlockMerge merge || _currentHunk < 0) return;
        try
        {
            var edit = merge.Plan(_bufferDiff, _leftBuffer.Snapshot(), _rightBuffer.Snapshot(), _currentHunk, toRight ? MergeDirection.LeftToRight : MergeDirection.RightToLeft);
            var pane = toRight ? RightCode : LeftCode;
            EditRequested(pane, edit); pane.SetCaret(edit.Offset + edit.Text.Length); pane.Focus();
            await _editTask;
        }
        catch (Exception ex) { StatusLabel.Text = "块复制失败：" + ex.Message; }
    }
    private void HistoryRequested(DiffPane pane, bool redo)
    {
        if (_syncActive || pane.Buffer is null || (redo ? !pane.Buffer.CanRedo : !pane.Buffer.CanUndo)) return;
        _activePane = pane;
        int caret = redo ? pane.Buffer.Redo() : pane.Buffer.Undo();
        ShowDraft(); pane.SetCaret(caret); pane.Focus(); UpdateEditState(); _editTask = RecompareBuffers(false);
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
            if (_folderResult is not null && count > 0) FolderSummary.ToolTip = "文件已保存；目录摘要可通过目录视图 F5 刷新。";
            _editTask = RecompareBuffers(false); await _editTask;
            StatusLabel.Text = count == 0 ? "没有需要保存的修改。" : $"已保存 {count} 个文件。";
            return true;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"已保存 {count} 个文件；保存未完成：{ex.Message}";
            if (!_arguments.Contains("--edit-smoke-test")) MessageBox.Show(this, StatusLabel.Text, "保存失败 — 编辑仍保留", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { _saving = false; UpdateEditState(); }
    }
    private async Task<bool> CanLeaveEdits()
    {
        if (_saving) { StatusLabel.Text = "正在保存，请稍候。"; return false; }
        if (!HasDirtyBuffers) return true;
        var choice = MessageBox.Show(this, "左右文件中有未保存的修改。\n\n是：保存修改后继续\n否：放弃修改\n取消：留在当前文件", "未保存的修改", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        return choice switch { MessageBoxResult.Yes => await SaveBuffers([_leftBuffer, _rightBuffer]) && !HasDirtyBuffers, MessageBoxResult.No => true, _ => false };
    }
    private async void EditorClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        if (!HasDirtyBuffers && !_saving) return;
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
