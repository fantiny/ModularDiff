using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ModularDiff.Contracts;

namespace ModularDiff.Gui.Controls;

public sealed partial class DiffPane
{
    public IEditBuffer? Buffer { get; private set; }
    public int CaretOffset => _caret;
    public event Action<DiffPane, TextEdit>? EditRequested;
    public event Action<DiffPane, bool>? HistoryRequested;
    public event Action<DiffPane, int>? SplitMenuRequested;

    /// <summary>Right-click on an editable cell: the host builds the line-splitter menu for that row.</summary>
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (Buffer is null || _rows.Count == 0) return;
        Focus();
        if (RowSourceLine(Math.Clamp((int)((e.GetPosition(this).Y + _verticalOffset) / RowHeight), 0, _rows.Count - 1)) >= 0)
            SplitMenuRequested?.Invoke(this, Math.Clamp((int)((e.GetPosition(this).Y + _verticalOffset) / RowHeight), 0, _rows.Count - 1));
        e.Handled = true;
    }
    /// <summary>Source line number shown at a row, or -1 when this side has no cell there.</summary>
    public int RowSourceLine(int rowIndex)
    {
        RefreshBufferCache();
        if (rowIndex < 0 || rowIndex >= _rows.Count) return -1;
        var cell = IsLeft ? _rows[rowIndex].Left : _rows[rowIndex].Right;
        return cell is not null && cell.LineNumber <= _documentLines.Count ? cell.LineNumber : -1;
    }
    public int LineCount { get { RefreshBufferCache(); return _documentLines.Count; } }
    public DocumentLine? Line(int sourceLine)
    {
        RefreshBufferCache();
        return sourceLine >= 1 && sourceLine <= _documentLines.Count ? _documentLines[sourceLine - 1] : null;
    }
    /// <summary>Source lines covered by the current selection (text selection in edit mode).
    /// The virtual EOF caret row does not count as a line.</summary>
    public IReadOnlyList<int> SelectedSourceLines()
    {
        RefreshBufferCache();
        if (Buffer is null || _documentLines.Count == 0) return [];
        int first = LineAt(Math.Min(_caret, _selectionAnchor)), last = LineAt(Math.Max(_caret, _selectionAnchor));
        if (first >= _documentLines.Count) return [];
        last = Math.Min(last, _documentLines.Count - 1);
        return [.. Enumerable.Range(first + 1, last - first + 1)];
    }
    private IReadOnlyList<DocumentLine> _documentLines = [];
    private Dictionary<int, int> _lineRows = [];
    private long _bufferVersion = -1;
    private int _caret, _selectionAnchor;
    private bool HasFinalCaretRow => Buffer is not null && (Buffer.Text.Length == 0 || Buffer.Text[^1] is '\r' or '\n');

    public void AttachBuffer(IEditBuffer? buffer)
    {
        Buffer = buffer; _bufferVersion = -1; _caret = _selectionAnchor = 0;
        RefreshBufferCache(); InvalidateMeasure(); InvalidateVisual();
    }
    private void RefreshBufferCache()
    {
        if (Buffer is null) { _documentLines = []; _lineRows.Clear(); return; }
        if (_bufferVersion != Buffer.Version)
        { _documentLines = DocumentLines.Parse(Buffer.Text); _bufferVersion = Buffer.Version; }
        _caret = Math.Clamp(_caret, 0, Buffer.Text.Length); _selectionAnchor = Math.Clamp(_selectionAnchor, 0, Buffer.Text.Length);
        _lineRows = [];
        for (int i = 0; i < _rows.Count; i++)
        { var cell = IsLeft ? _rows[i].Left : _rows[i].Right; if (cell is not null) _lineRows[cell.LineNumber] = i; }
    }
    private int LineAt(int offset)
    {
        if (_documentLines.Count == 0) return 0;
        if (Buffer is not null && offset == Buffer.Text.Length && HasFinalCaretRow) return _documentLines.Count;
        int lo = 0, hi = _documentLines.Count;
        while (lo < hi) { int mid = (lo + hi) / 2; if (_documentLines[mid].Offset <= offset) lo = mid + 1; else hi = mid; }
        return Math.Max(0, lo - 1);
    }
    private int OffsetForRow(int row)
    {
        for (int i = Math.Max(0, row); i < _rows.Count; i++)
        {
            var cell = IsLeft ? _rows[i].Left : _rows[i].Right;
            if (cell is not null && cell.LineNumber <= _documentLines.Count) return _documentLines[cell.LineNumber - 1].Offset;
        }
        return Buffer?.Text.Length ?? 0;
    }
    public void SetCaret(int offset, bool extend = false, bool reveal = true)
    {
        if (Buffer is null) return;
        RefreshBufferCache();
        _caret = Math.Clamp(offset, 0, Buffer.Text.Length);
        if (!extend) _selectionAnchor = _caret;
        int line = LineAt(_caret), row = _lineRows.GetValueOrDefault(line + 1, _rows.Count);
        _selected = row;
        if (reveal)
        {
            double y = row * RowHeight;
            if (y < _verticalOffset) SetVerticalOffset(y);
            else if (y + RowHeight > _verticalOffset + _viewport.Height) SetVerticalOffset(y + RowHeight - _viewport.Height);
            string prefix = line < _documentLines.Count ? _documentLines[line].Text[..Math.Clamp(_caret - _documentLines[line].Offset, 0, _documentLines[line].Text.Length)] : "";
            double x = Format(ExpandTabs(prefix), Ink).WidthIncludingTrailingWhitespace;
            if (x < _horizontalOffset) SetHorizontalOffset(Math.Max(0, x - 20));
            else if (x > _horizontalOffset + _viewport.Width - 90) SetHorizontalOffset(x - Math.Max(20, _viewport.Width - 110));
        }
        InvalidateVisual();
    }
    private static string ExpandTabs(string text)
    {
        var result = new System.Text.StringBuilder();
        foreach (char c in text) { if (c == '\t') result.Append(' ', 4 - result.Length % 4); else result.Append(c); }
        return result.ToString();
    }
    private int OffsetAtPoint(Point point)
    {
        int row = Math.Max(0, (int)((point.Y + _verticalOffset) / RowHeight));
        if (row >= _rows.Count) return Buffer?.Text.Length ?? 0;
        var cell = IsLeft ? _rows[row].Left : _rows[row].Right;
        if (cell is null || cell.LineNumber > _documentLines.Count) return OffsetForRow(row);
        string text = _documentLines[cell.LineNumber - 1].Text;
        double distance = Math.Max(0, point.X + _horizontalOffset - 64);
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (Format(ExpandTabs(text[..(mid + 1)]), Ink).WidthIncludingTrailingWhitespace < distance) lo = mid + 1; else hi = mid;
        }
        if (lo < text.Length && lo > 0 && char.IsLowSurrogate(text[lo])) lo--;
        return _documentLines[cell.LineNumber - 1].Offset + lo;
    }
    private void EditMouseDown(MouseButtonEventArgs e)
    {
        RefreshBufferCache(); var point = e.GetPosition(this);
        int row = Math.Max(0, (int)((point.Y + _verticalOffset) / RowHeight));
        if (row < _rows.Count && _rows[row].HiddenLines > 0) { ExpandContext?.Invoke(); e.Handled = true; return; }
        int offset = OffsetAtPoint(point);
        SetCaret(offset, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), false);
        if (point.X < 54)
        {
            // Number-column click: select the whole line; Shift extends from the anchor line so
            // multi-line selections feed the batch line-split menu.
            int line = LineAt(offset);
            if (line < _documentLines.Count)
            {
                int first = line, last = line;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { int anchor = LineAt(_selectionAnchor); first = Math.Min(anchor, line); last = Math.Max(anchor, line); }
                else _selectionAnchor = _documentLines[first].Offset;
                _caret = _documentLines[last].Offset + _documentLines[last].Length;
            }
        }
        else if (e.ClickCount == 2 && Buffer is not null)
        {
            int begin = offset, end = offset;
            while (begin > 0 && IsWord(Buffer.Text[begin - 1])) begin--;
            while (end < Buffer.Text.Length && IsWord(Buffer.Text[end])) end++;
            _selectionAnchor = begin; _caret = end;
        }
        CaptureMouse(); RowSelected?.Invoke(Math.Min(row, Math.Max(0, _rows.Count - 1))); InvalidateVisual(); e.Handled = true;
    }
    private void EditMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(this);
        if (point.Y < 0) LineUp(); else if (point.Y > _viewport.Height) LineDown();
        SetCaret(OffsetAtPoint(point), true, false);
    }
    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (Buffer is null || string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl)) return;
        ReplaceSelection(e.Text, "Typing"); e.Handled = true;
    }
    public void ReplaceSelection(string text, string label = "Paste")
    {
        if (Buffer is null) return;
        int start = Math.Min(_caret, _selectionAnchor), length = Math.Abs(_caret - _selectionAnchor);
        // Host applies the edit synchronously, then recomputes the diff asynchronously.
        EditRequested?.Invoke(this, new(start, length, text, label));
        SetCaret(start + text.Length);
    }
    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
    private int PreviousPosition(int position)
    {
        if (Buffer is null || position <= 0) return 0;
        string text = Buffer.Text;
        return position >= 2 && ((text[position - 1] == '\n' && text[position - 2] == '\r') || (char.IsLowSurrogate(text[position - 1]) && char.IsHighSurrogate(text[position - 2]))) ? position - 2 : position - 1;
    }
    private int NextPosition(int position)
    {
        if (Buffer is null || position >= Buffer.Text.Length) return Buffer?.Text.Length ?? 0;
        string text = Buffer.Text;
        return position + 1 < text.Length && ((text[position] == '\r' && text[position + 1] == '\n') || (char.IsHighSurrogate(text[position]) && char.IsLowSurrogate(text[position + 1]))) ? position + 2 : position + 1;
    }
    private bool EditKeyDown(KeyEventArgs e)
    {
        if (Buffer is null) return false;
        RefreshBufferCache(); bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool handled = true;
        try
        {
            if (control && e.Key == Key.A) { _selectionAnchor = 0; _caret = Buffer.Text.Length; InvalidateVisual(); }
            else if (control && e.Key is Key.C or Key.X)
            { string selected = SelectedText(); if (selected.Length > 0) { Clipboard.SetText(selected); if (e.Key == Key.X) ReplaceSelection("", "Cut"); } }
            else if (control && e.Key == Key.V) { if (Clipboard.ContainsText()) ReplaceSelection(Clipboard.GetText(), "Paste"); }
            else if (control && e.Key is Key.Z or Key.Y) HistoryRequested?.Invoke(this, e.Key == Key.Y || shift);
            else if (e.Key == Key.Enter) ReplaceSelection(Buffer.NewLine, "New line");
            else if (e.Key == Key.Tab)
            {
                if (!shift) ReplaceSelection("\t", "Indent");
                else
                {
                    int index = LineAt(_caret);
                    if (index < _documentLines.Count)
                    {
                        var line = _documentLines[index]; int count = line.Text.StartsWith('\t') ? 1 : Math.Min(4, line.Text.TakeWhile(c => c == ' ').Count());
                        if (count > 0) { int caret = _caret; EditRequested?.Invoke(this, new(line.Offset, count, "", "Outdent")); SetCaret(Math.Max(line.Offset, caret - count)); }
                    }
                }
            }
            else if (e.Key is Key.Back or Key.Delete)
            {
                if (_caret == _selectionAnchor)
                { if (e.Key == Key.Back) _selectionAnchor = PreviousPosition(_caret); else _selectionAnchor = NextPosition(_caret); }
                ReplaceSelection("", e.Key == Key.Back ? "Backspace" : "Delete");
            }
            else if (e.Key is Key.Left or Key.Right)
            {
                int next = e.Key == Key.Left ? PreviousPosition(_caret) : NextPosition(_caret);
                if (control)
                {
                    if (e.Key == Key.Left) { while (next > 0 && !IsWord(Buffer.Text[next])) next = PreviousPosition(next); while (next > 0 && IsWord(Buffer.Text[next - 1])) next = PreviousPosition(next); }
                    else { while (next < Buffer.Text.Length && IsWord(Buffer.Text[next])) next = NextPosition(next); while (next < Buffer.Text.Length && !IsWord(Buffer.Text[next])) next = NextPosition(next); }
                }
                SetCaret(next, shift);
            }
            else if (e.Key is Key.Home or Key.End)
            {
                int index = LineAt(_caret);
                int offset = control ? e.Key == Key.Home ? 0 : Buffer.Text.Length : index >= _documentLines.Count ? Buffer.Text.Length
                    : _documentLines[index].Offset + (e.Key == Key.Home ? 0 : _documentLines[index].Text.Length);
                SetCaret(offset, shift);
            }
            else if (e.Key is Key.Up or Key.Down)
            {
                int index = LineAt(_caret), column = index < _documentLines.Count ? _caret - _documentLines[index].Offset : 0;
                int target = Math.Clamp(index + (e.Key == Key.Up ? -1 : 1), 0, _documentLines.Count);
                SetCaret(target < _documentLines.Count ? _documentLines[target].Offset + Math.Min(column, _documentLines[target].Text.Length) : Buffer.Text.Length, shift);
            }
            else handled = false;
        }
        catch (System.Runtime.InteropServices.ExternalException) { /* A temporarily busy clipboard must not terminate the editor. */ }
        if (handled) { e.Handled = true; if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End) RowSelected?.Invoke(_selected); InvalidateVisual(); }
        return handled;
    }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
    private void DrawTextSelection(DrawingContext dc, TextCell cell, FormattedText formatted, int[] positions, Point origin)
    {
        if (Buffer is null || cell.LineNumber > _documentLines.Count || _caret == _selectionAnchor) return;
        var line = _documentLines[cell.LineNumber - 1];
        int start = Math.Clamp(Math.Min(_caret, _selectionAnchor) - line.Offset, 0, line.Text.Length);
        int end = Math.Clamp(Math.Max(_caret, _selectionAnchor) - line.Offset, 0, line.Text.Length);
        if (end > start)
        {
            var geometry = formatted.BuildHighlightGeometry(origin, positions[start], positions[end] - positions[start]);
            if (geometry is not null) dc.DrawGeometry(Selected, null, geometry);
        }
    }
    private void DrawTextCaret(DrawingContext dc, TextCell cell, string text, int[] positions, Point origin)
    {
        if (Buffer is null || !IsKeyboardFocused || cell.LineNumber > _documentLines.Count || LineAt(_caret) != cell.LineNumber - 1) return;
        int column = Math.Clamp(_caret - _documentLines[cell.LineNumber - 1].Offset, 0, cell.Text.Length);
        double x = origin.X + Format(text[..positions[column]], Ink).WidthIncludingTrailingWhitespace;
        dc.DrawLine(new Pen(Brush("#254E86"), 1.3), new(x, origin.Y), new(x, origin.Y + _fontSize + 3));
    }
    private void DrawFinalCaretRow(DrawingContext dc)
    {
        if (!HasFinalCaretRow) return;
        double y = _rows.Count * RowHeight - _verticalOffset;
        if (y < -RowHeight || y > RenderSize.Height) return;
        dc.DrawRectangle(Brush("#F7F8FA"), null, new Rect(0, y, 54, RowHeight));
        var number = Format((_documentLines.Count + 1).ToString(), Muted, 11); dc.DrawText(number, new(46 - number.Width, y + 4));
        if (IsKeyboardFocused && Buffer is not null && _caret == Buffer.Text.Length)
            dc.DrawLine(new Pen(Brush("#254E86"), 1.3), new(64 - _horizontalOffset, y + 2), new(64 - _horizontalOffset, y + _fontSize + 5));
    }
}
