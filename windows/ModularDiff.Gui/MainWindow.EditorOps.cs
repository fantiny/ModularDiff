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
        if (selected.Count == 0) { StatusLabel.Text = "请先选中要复制的行。"; return; }

        try
        {
            // Source content for the selected lines.
            string[] sourceLines = source.Buffer.Text.Split('\n');
            int srcFirst = selected[0] - 1, srcLast = selected[^1] - 1;
            string replacement = string.Join("\n", sourceLines[srcFirst..(srcLast + 1)]);

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
            if (destFirst < 0) { StatusLabel.Text = "选中区域在对侧没有对应位置（可能全部是新增行）。"; return; }

            // Build the TextEdit on the destination buffer.
            var firstDoc = dest.Line(destFirst);
            var lastDoc = dest.Line(destLast);
            if (firstDoc is null || lastDoc is null) return;
            int offset = firstDoc.Offset;
            int length = lastDoc.Offset + lastDoc.Length - offset;
            dest.Buffer.Apply(new TextEdit(offset, length, replacement, $"复制选中行 {(toRight ? "左→右" : "右→左")}"));
            _activePane = dest;
            AfterProgrammaticEdit(dest, $"已复制 {selected.Count} 行到{(toRight ? "右侧" : "左侧")}。");
        }
        catch (Exception ex) { StatusLabel.Text = "复制选中行失败：" + ex.Message; }
    }

    // ─── 1b: single-line copy to other side ───

    /// <summary>Copies the current cursor line to the same aligned position on the other side.</summary>
    private async void CopyLineToSide(bool toRight)
    {
        var source = toRight ? LeftCode : RightCode;
        var dest = toRight ? RightCode : LeftCode;
        if (source.Buffer is null || dest.Buffer is null || _presentation is null) return;
        var selected = source.SelectedSourceLines();
        if (selected.Count == 0) { StatusLabel.Text = "请将光标放在要复制的行上。"; return; }
        try
        {
            int srcLine = selected[0];
            var doc = source.Line(srcLine);
            if (doc is null) return;
            string replacement = doc.Text;
            var srcCell = _presentation.Rows
                .Select(r => toRight ? r.Left : r.Right)
                .FirstOrDefault(c => c?.LineNumber == srcLine);
            var dstCell = _presentation.Rows
                .Where(r => (toRight ? r.Left : r.Right)?.LineNumber == srcLine)
                .Select(r => toRight ? r.Right : r.Left)
                .FirstOrDefault();
            if (dstCell is null) { StatusLabel.Text = "该行在对侧没有对应位置。"; return; }
            var destDoc = dest.Line(dstCell.LineNumber);
            if (destDoc is null) return;
            dest.Buffer.Apply(new TextEdit(destDoc.Offset, destDoc.Length, replacement,
                $"复制行 {(toRight ? "左→右" : "右→左")}"));
            _activePane = dest;
            AfterProgrammaticEdit(dest, "已复制 1 行。");
        }
        catch (Exception ex) { StatusLabel.Text = "复制行失败：" + ex.Message; }
    }

    // ─── 1e: Go To line number ───

    private void GoToLine()
    {
        var pane = _activePane ?? LeftCode;
        if (pane.Buffer is null) { StatusLabel.Text = "请先打开一个文件。"; return; }
        string? input = PromptLineNumber(pane.LineCount);
        if (!int.TryParse(input, out int line) || line < 1 || line > pane.LineCount)
        { if (input is not null) StatusLabel.Text = $"行号必须在 1–{pane.LineCount} 之间。"; return; }
        pane.Focus();
        pane.SetCaret(pane.Line(line)!.Offset);
        pane.SelectRow(pane.SelectedRow);
    }

    private string? PromptLineNumber(int max)
    {
        var owner = this;
        var dialog = new Window
        {
            Title = "跳转到行", Width = 320, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = owner, ResizeMode = ResizeMode.NoResize,
        };
        var box = new TextBox { Text = "1", Margin = new Thickness(12, 4, 12, 8) };
        var hint = new TextBlock { Text = $"行号 (1–{max})：", Margin = new Thickness(12, 12, 12, 0) };
        var ok = new Button { Content = "跳转", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", Width = 60, IsCancel = true };
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
            StatusLabel.Text = $"书签 {number} 已清除。";
        }
        else
        {
            _bookmarks[number] = (pane, line);
            StatusLabel.Text = $"书签 {number} → {pane.Line(line)?.Text}";
        }
    }

    private void GoToBookmark(int number)
    {
        if (!_bookmarks.TryGetValue(number, out var mark)) { StatusLabel.Text = $"书签 {number} 未设置。"; return; }
        mark.Pane.Focus();
        var doc = mark.Pane.Line(mark.Line);
        if (doc is null) { StatusLabel.Text = $"书签 {number} 的行已不存在。"; return; }
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
        if (hunkId < 0) { StatusLabel.Text = "光标不在差异块内。"; return; }
        var hunk = _presentation.Hunks[hunkId];
        // Select all rows in this hunk via the number-column mechanism on the active pane.
        pane.SelectColumn(hunk.StartRow, hunk.EndRow, 0, int.MaxValue);
        StatusLabel.Text = $"已选中差异块 {hunkId + 1}（{hunk.EndRow - hunk.StartRow + 1} 行）。";
    }

    // ─── 2c: file-level text transforms (right-click menu items) ───

    private void TrimTrailingWhitespace(DiffPane pane)
    {
        if (pane.Buffer is null) return;
        try
        {
            string[] lines = pane.Buffer.Text.Split('\n');
            for (int i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd(' ', '\t', '\r');
            pane.Buffer.Apply(new TextEdit(0, pane.Buffer.Text.Length, string.Join("\n", lines), "修剪行尾空白"));
            AfterProgrammaticEdit(pane, "已修剪行尾空白。");
        }
        catch (Exception ex) { StatusLabel.Text = "修剪失败：" + ex.Message; }
    }

    private void ConvertTabsToSpaces(DiffPane pane)
    {
        if (pane.Buffer is null) return;
        try
        {
            string text = pane.Buffer.Text.Replace("\t", new string(' ', 4));
            pane.Buffer.Apply(new TextEdit(0, pane.Buffer.Text.Length, text, "Tab → 空格"));
            AfterProgrammaticEdit(pane, "已将 Tab 转为空格。");
        }
        catch (Exception ex) { StatusLabel.Text = "转换失败：" + ex.Message; }
    }

    private void ConvertSpacesToTabs(DiffPane pane)
    {
        if (pane.Buffer is null) return;
        try
        {
            string[] lines = pane.Buffer.Text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                int spaces = lines[i].TakeWhile(c => c == ' ').Count();
                lines[i] = new string('\t', spaces / 4) + lines[i][Math.Min(spaces - spaces % 4, lines[i].Length)..];
            }
            pane.Buffer.Apply(new TextEdit(0, pane.Buffer.Text.Length, string.Join("\n", lines), "空格 → Tab"));
            AfterProgrammaticEdit(pane, "已将前导空格转为 Tab。");
        }
        catch (Exception ex) { StatusLabel.Text = "转换失败：" + ex.Message; }
    }

    private void ToggleLineEndings(DiffPane pane)
    {
        if (pane.Buffer is null) return;
        try
        {
            string text = pane.Buffer.Text;
            string converted = text.Contains("\r\n") ? text.Replace("\r\n", "\n") : text.Replace("\n", "\r\n");
            string label = text.Contains("\r\n") ? "CRLF → LF" : "LF → CRLF";
            pane.Buffer.Apply(new TextEdit(0, text.Length, converted, label));
            AfterProgrammaticEdit(pane, $"已切换行尾风格：{label}。");
        }
        catch (Exception ex) { StatusLabel.Text = "行尾切换失败：" + ex.Message; }
    }

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
            StatusLabel.Text = "已切换为上下布局。";
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
            StatusLabel.Text = "已切换为左右布局。";
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
                pane.Buffer.Apply(new TextEdit(doc.Offset, length, "", "删除行"));
                AfterProgrammaticEdit(pane, "已删除当前行。");
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
