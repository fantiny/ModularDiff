using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ModularDiff.Contracts;
using ModularDiff.Gui.Controls;

namespace ModularDiff.Gui;

/// <summary>Editor operations beyond basic typing: arbitrary selection copy to the other side,
/// single-line copy, Go To, bookmarks, section select, and file-level text transforms.</summary>
public partial class MainWindow
{
    // ─── 1a: arbitrary selection → copy to other side ───

    /// <summary>Copies the currently selected source lines to the other pane, replacing the
    /// aligned destination lines — works on ANY selection, not just difference hunks.</summary>
    private async void CopySelectionToSide(bool toRight)
    {
        var source = toRight ? LeftCode : RightCode;
        var dest = toRight ? RightCode : LeftCode;
        if (source.Buffer is null || dest.Buffer is null || _presentation is null) return;
        var selected = source.SelectedSourceLines();
        if (selected.Count == 0) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_select_copy_rows"); return; }

        try
        {
            // Source content for the selected lines, rebuilt in the DESTINATION's ending style.
            // TextEdit length spans whole lines (DocumentLine.Length includes the ending), so the
            // replacement must end with a terminator too — otherwise the next line gets glued on.
            var sourceDocs = DocumentLines.Parse(source.Buffer.Text);
            int srcFirst = selected[0] - 1, srcLast = selected[^1] - 1;
            string destEol = dest.Buffer.Text.Contains("\r\n") ? "\r\n" : "\n";   // 目标侧行尾风格
            string replacement = string.Concat(Enumerable.Range(srcFirst, srcLast - srcFirst + 1)
                .Select(i => sourceDocs[i].Text + destEol));

            // Find the destination range through the aligned rows.
            int destFirst = -1, destLast = -1;
            foreach (var row in _presentation.Rows)
            {
                var srcCell = toRight ? row.Left : row.Right;
                var dstCell = toRight ? row.Right : row.Left;
                if (srcCell is null || srcCell.LineNumber < selected[0] || srcCell.LineNumber > selected[^1]) continue;
                if (dstCell is not null)
                {
                    if (destFirst < 0) destFirst = dstCell.LineNumber;
                    destLast = dstCell.LineNumber;
                }
            }
            if (destFirst < 0) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_no_dest_selection"); return; }

            // Build the TextEdit on the destination buffer.
            var firstDoc = dest.Line(destFirst);
            var lastDoc = dest.Line(destLast);
            if (firstDoc is null || lastDoc is null) return;
            int offset = firstDoc.Offset;
            int length = lastDoc.Offset + lastDoc.Length - offset;
            dest.Buffer.Apply(new TextEdit(offset, length, replacement, ModularDiff.Gui.Controls.Lang.Get("edit_desc_copy_sel", ModularDiff.Gui.Controls.Lang.Get(toRight ? "dir_ltr" : "dir_rtl"))));
            _activePane = dest;
            AfterProgrammaticEdit(dest, ModularDiff.Gui.Controls.Lang.Get("status_copied_sel_rows", selected.Count, ModularDiff.Gui.Controls.Lang.Get(toRight ? "side_right" : "side_left")));
        }
        catch (Exception ex) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_copy_sel_failed", ex.Message); }
    }

    // ─── 1b: single-line copy to other side ───

    /// <summary>Copies the current cursor line to the same aligned position on the other side.</summary>
    private async void CopyLineToSide(bool toRight)
    {
        var source = toRight ? LeftCode : RightCode;
        var dest = toRight ? RightCode : LeftCode;
        if (source.Buffer is null || dest.Buffer is null || _presentation is null) return;
        var selected = source.SelectedSourceLines();
        if (selected.Count == 0) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_place_cursor_line"); return; }
        try
        {
            int srcLine = selected[0];
            var doc = source.Line(srcLine);
            if (doc is null) return;
            var srcCell = _presentation.Rows
                .Select(r => toRight ? r.Left : r.Right)
                .FirstOrDefault(c => c?.LineNumber == srcLine);
            var dstCell = _presentation.Rows
                .Where(r => (toRight ? r.Left : r.Right)?.LineNumber == srcLine)
                .Select(r => toRight ? r.Right : r.Left)
                .FirstOrDefault();
            if (dstCell is null) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_no_peer_line"); return; }
            var destDoc = dest.Line(dstCell.LineNumber);
            if (destDoc is null) return;
            // 被替换的范围含目标行的行尾符(DocumentLine.Length = Text + Ending),替换文本必须带上
            // 同样的行尾 —— 此前裸文本替换会把下一行拼接进来(每次 Ctrl+L 损坏一次缓冲区)。
            string destEol = dest.Buffer.Text.Contains("\r\n") ? "\r\n" : "\n";
            dest.Buffer.Apply(new TextEdit(destDoc.Offset, destDoc.Length, doc.Text + destEol,
                ModularDiff.Gui.Controls.Lang.Get("edit_desc_copy_line", ModularDiff.Gui.Controls.Lang.Get(toRight ? "dir_ltr" : "dir_rtl"))));
            _activePane = dest;
            AfterProgrammaticEdit(dest, ModularDiff.Gui.Controls.Lang.Get("status_copied_one_row"));
        }
        catch (Exception ex) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_copy_line_failed", ex.Message); }
    }

    // ─── 1e: Go To line number ───

    private void GoToLine()
    {
        var pane = _activePane ?? LeftCode;
        if (pane.Buffer is null) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_open_file_first"); return; }
        string? input = PromptLineNumber(pane.LineCount);
        if (!int.TryParse(input, out int line) || line < 1 || line > pane.LineCount)
        { if (input is not null) StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_line_range", pane.LineCount); return; }
        pane.Focus();
        pane.SetCaret(pane.Line(line)!.Offset);
        pane.SelectRow(pane.SelectedRow);
    }

    private string? PromptLineNumber(int max)
    {
        var owner = this;
        var dialog = new Window
        {
            Title = ModularDiff.Gui.Controls.Lang.Get("dlg_goto_line_title"), Width = 320, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = owner, ResizeMode = ResizeMode.NoResize,
        };
        var box = new TextBox { Text = "1", Margin = new Thickness(12, 4, 12, 8) };
        var hint = new TextBlock { Text = ModularDiff.Gui.Controls.Lang.Get("dlg_goto_line_hint", max), Margin = new Thickness(12, 12, 12, 0) };
        var ok = new Button { Content = ModularDiff.Gui.Controls.Lang.Get("dlg_goto"), Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = ModularDiff.Gui.Controls.Lang.Get("dlg_cancel"), Width = 60, IsCancel = true };
        string? result = null;
        ok.Click += (_, _) => { if (int.TryParse(box.Text.Trim(), out _)) { result = box.Text.Trim(); dialog.Close(); } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var panel = new StackPanel();
        panel.Children.Add(hint); panel.Children.Add(box); panel.Children.Add(buttons);
        dialog.Content = panel;
        box.SelectAll(); box.Loaded += (_, _) => box.Focus();
        dialog.ShowDialog();
        return result;
    }

    // ─── 2a: bookmarks (numbered 0-9) ───

    private readonly Dictionary<int, (DiffPane Pane, int Line)> _bookmarks = [];

    private void ToggleBookmark(int number)
    {
        var pane = _activePane ?? LeftCode;
        if (pane.Buffer is null) return;
        int line = pane.SelectedSourceLines().FirstOrDefault();
        if (line < 1) return;
        if (_bookmarks.TryGetValue(number, out var existing) && existing.Pane == pane && existing.Line == line)
        {
            _bookmarks.Remove(number);
            StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_bookmark_cleared", number);
        }
        else
        {
            _bookmarks[number] = (pane, line);
            StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_bookmark_set", number, pane.Line(line)?.Text ?? "");
        }
    }

    private void GoToBookmark(int number)
    {
        if (!_bookmarks.TryGetValue(number, out var mark)) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_bookmark_missing", number); return; }
        mark.Pane.Focus();
        var doc = mark.Pane.Line(mark.Line);
        if (doc is null) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_bookmark_gone", number); return; }
        mark.Pane.SetCaret(doc.Offset);
        int row = mark.Pane.SelectedRow;
        LeftCode.SelectRow(row); RightCode.SelectRow(row); CodeSelected(row);
    }

    // ─── 2b: select current difference section ───

    private void SelectSection()
    {
        if (_presentation is null || _activePane is not { } pane) return;
        int currentRow = pane.SelectedRow;
        if (currentRow < 0 || currentRow >= _presentation.Rows.Count) return;
        int hunkId = _presentation.Rows[currentRow].HunkId;
        if (hunkId < 0) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_not_in_hunk"); return; }
        // DiffHunk.StartRow/EndRow 是展示行空间,而 SelectColumn 需要文档行号 —— 两个坐标系在
        // 出现不成对行(增/删)后即分叉。经该块的行解析真实行号(此前直接传展示行号,选错行后
        // 打字会改错行)。
        bool leftPane = ReferenceEquals(pane, LeftCode);
        var cells = _presentation.Rows.Where(r => r.HunkId == hunkId)
            .Select(r => leftPane ? r.Left : r.Right).OfType<TextCell>().ToList();
        if (cells.Count == 0) return;
        pane.SelectColumn(cells[0].LineNumber, cells[^1].LineNumber, 0, int.MaxValue);
        StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_hunk_selected", hunkId + 1, cells.Count);
    }

    // File transforms moved to FileTypes plugin (GenericFileTools provider).

    // ─── 2d: over-under layout toggle ───

    private bool _overUnder;
    private void ToggleLayoutClick(object sender, RoutedEventArgs e)
    {
        _overUnder = !_overUnder;
        var grid = TextGrid;
        if (_overUnder)
        {
            grid.ColumnDefinitions.Clear();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 100 });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 100 });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(LeftCodeScroll, 0); Grid.SetRow(LeftCodeScroll, 0);
            Grid.SetColumn(TextSplitter, 1); Grid.SetRow(TextSplitter, 1);
            TextSplitter.Width = double.NaN; TextSplitter.Height = 6;
            TextSplitter.HorizontalAlignment = HorizontalAlignment.Stretch; TextSplitter.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(RightCodeScroll, 2); Grid.SetRow(RightCodeScroll, 2);
            Grid.SetColumn(Overview, 3); Grid.SetRow(Overview, 3);
            StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_layout_vertical");
        }
        else
        {
            grid.RowDefinitions.Clear();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 280 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 280 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetRow(LeftCodeScroll, 0); Grid.SetColumn(LeftCodeScroll, 0);
            Grid.SetRow(TextSplitter, 0); Grid.SetColumn(TextSplitter, 1);
            TextSplitter.Height = double.NaN; TextSplitter.Width = 6;
            TextSplitter.VerticalAlignment = VerticalAlignment.Stretch; TextSplitter.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetRow(RightCodeScroll, 0); Grid.SetColumn(RightCodeScroll, 2);
            Grid.SetRow(Overview, 0); Grid.SetColumn(Overview, 3);
            StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_layout_horizontal");
        }
        foreach (UIElement child in grid.Children)
            if (child is not ScrollViewer && child is not GridSplitter && child is not DiffOverview)
            {
                // Overlay elements (empty state, hex, picture) span the entire grid either way.
                Grid.SetRow(child, 0); Grid.SetColumn(child, 0); Grid.SetColumnSpan(child, 4); Grid.SetRowSpan(child, 4);
            }
    }

    // ─── keyboard wiring ───

    private async Task<bool> EditorOpsShortcut(KeyEventArgs e)
    {
        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var pane = _activePane ?? LeftCode;
        // Copy selection or line to other side
        if (control && e.Key is Key.Right or Key.Left && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            // Ctrl+Right/Left already handled by EditingShortcut for block copy; add Alt for selection copy
            return false;
        }
        if (control && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && e.Key is Key.Right or Key.Left)
        {
            e.Handled = true; CopySelectionToSide(e.Key == Key.Right); return true;
        }
        if (control && e.Key == Key.L && !shift)
        {
            e.Handled = true; CopyLineToSide(pane == LeftCode); return true;
        }
        // Go To
        if (control && e.Key == Key.G && !shift)
        {
            e.Handled = true; GoToLine(); return true;
        }
        // Bookmarks: Ctrl+Shift+0-9 to set, Ctrl+0-9 to jump
        if (control && e.Key is >= Key.D0 and <= Key.D9)
        {
            int number = e.Key - Key.D0;
            e.Handled = true;
            if (shift) ToggleBookmark(number); else GoToBookmark(number);
            return true;
        }
        // Delete line
        if (control && shift && e.Key == Key.K && pane.Buffer is not null)
        {
            e.Handled = true;
            int line = pane.SelectedSourceLines().FirstOrDefault();
            var doc = pane.Line(line);
            if (doc is not null)
            {
                int length = doc.Length;
                if (doc.Offset + length < pane.Buffer.Text.Length) length += pane.Buffer.NewLine.Length;
                pane.Buffer.Apply(new TextEdit(doc.Offset, length, "", ModularDiff.Gui.Controls.Lang.Get("edit_desc_delete_line")));
                AfterProgrammaticEdit(pane, ModularDiff.Gui.Controls.Lang.Get("status_deleted_row"));
            }
            return true;
        }
        // Select Section
        if (control && e.Key == Key.E && !shift)
        {
            e.Handled = true; SelectSection(); return true;
        }
        return false;
    }
}
