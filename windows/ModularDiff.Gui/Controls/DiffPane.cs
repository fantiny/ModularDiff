using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ModularDiff.Contracts;

namespace ModularDiff.Gui.Controls;

/// <summary>Viewport-only rendering: both panes consume the same aligned rows and scroll in pixels.</summary>
public sealed partial class DiffPane : FrameworkElement, IScrollInfo
{
    private IReadOnlyList<AlignedRow> _rows = [];
    private double _horizontalOffset, _verticalOffset, _extentWidth = 100;
    private Size _viewport;
    private int _anchor = -1, _selected = -1;
    private double _fontSize = 14;
    private double[] _rowTops = [0];
    private double[] _rowHeights = [0];
    private double _measuredCharWidth;
    private bool _wrapLines;

    /// <summary>Wrap long lines to the viewport (Alt+Z). Off by default: identical layout to before.
    /// Both sides compute rows independently but a row's height is the max of its cells, so the
    /// two panes stay aligned — like Beyond Compare's wrapped view.</summary>
    public bool WrapLines
    {
        get => _wrapLines;
        set { if (_wrapLines == value) return; _wrapLines = value; LayoutRows(); InvalidateVisual(); }
    }
    public int WrapColumns { get; private set; } = -1;
    public double CharWidth { get { if (_measuredCharWidth == 0) _measuredCharWidth = Format("MMMMMMMMMM", Ink).WidthIncludingTrailingWhitespace / 10; return _measuredCharWidth; } }
    private static readonly Typeface Typeface = new("Consolas");
    private static readonly Regex Syntax = new("//.*$|\"(?:\\\\.|[^\"\\\\])*\"|\\b(?:public|private|protected|internal|static|class|record|namespace|using|return|new|void|int|long|string|bool|if|else|for|foreach|while|async|await|var|null|true|false|const|def|function|import|from|let)\\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(30));
    private static readonly Dictionary<string, Brush> Palette = [];
    private static Brush Brush(string color)
    {
        if (Palette.TryGetValue(color, out var brush)) return brush;
        brush = (Brush)new BrushConverter().ConvertFromString(color)!; brush.Freeze(); Palette.Add(color, brush); return brush;
    }
    private static readonly Brush Ink = Brush("#253041"), Muted = Brush("#8793A4"), Border = Brush("#DEE3EA"), Ghost = Brush("#F2F4F7"), Selected = Brush("#D9E8FF");
    private static readonly Brush Modified = Brush("#FFF6D6"), Deleted = Brush("#FFF0F0"), Inserted = Brush("#ECF7EF"), ChangedText = Brush("#A52D22"), ChangedBackground = Brush("#FFD4BC");
    public bool IsLeft { get; set; }
    public bool ShowEndings { get; set; } = true;
    public double RowHeight => Math.Ceiling(_fontSize * 1.55);
    public int SelectedRow => _selected;
    public IReadOnlyList<AlignedRow> Rows => _rows;
    public event Action<DiffPane>? ViewChanged;
    public event Action<int>? RowSelected;
    public event Action? ExpandContext;

    public DiffPane()
    {
        Focusable = true; ClipToBounds = true; Cursor = Cursors.IBeam;
        SnapsToDevicePixels = true;
    }

    public void SetRows(IReadOnlyList<AlignedRow> rows, bool reset = true)
    {
        _rows = rows;
        RefreshBufferCache();
        int maxColumns = 20;
        foreach (var row in rows)
        {
            foreach (var cell in new[] { row.Left, row.Right })
            {
            if (cell is null) continue;
            // Conservative Unicode/tab estimate keeps the horizontal scrollbar wide enough.
            int columns = 0;
            foreach (char c in cell.Text) columns += c == '\t' ? 4 : c > 0x2FFF ? 2 : 1;
            maxColumns = Math.Max(maxColumns, columns);
            }
        }
        _extentWidth = 80 + maxColumns * _fontSize * .64;
        LayoutRows();
        if (reset) _anchor = _selected = -1;
        if (reset) _horizontalOffset = _verticalOffset = 0;
        CoerceOffsets(); InvalidateMeasure(); InvalidateVisual(); ScrollOwner?.InvalidateScrollInfo();
    }

    /// <summary>Recomputes per-row heights (and wrap columns) from the current viewport size.
    /// Non-wrapped mode keeps one visual line per row — byte-for-byte the previous layout.</summary>
    public void LayoutRows()
    {
        WrapColumns = _wrapLines ? Math.Max(10, (int)((_viewport.Width - 64) / CharWidth)) : -1;
        int count = _rows.Count;
        _rowTops = new double[count];
        _rowHeights = new double[count];
        double top = 0;
        for (int i = 0; i < count; i++)
        {
            double height = RowHeight;
            if (_wrapLines && _rows[i].HiddenLines == 0)
            {
                int lines = Math.Max(VisualLines(_rows[i].Left), VisualLines(_rows[i].Right));
                height = lines * RowHeight;
            }
            _rowTops[i] = top;
            _rowHeights[i] = height;
            top += height;
        }
    }

    private int VisualLines(TextCell? cell)
    {
        if (cell is null || WrapColumns <= 0) return 1;
        int columns = 0;
        foreach (char c in cell.Text) columns += c == '\t' ? 4 : c > 0x2FFF ? 2 : 1;
        return Math.Max(1, (columns + WrapColumns - 1) / WrapColumns);
    }

    /// <summary>Row whose top is at or just above the pixel offset (binary search over the prefix sums).</summary>
    public int RowAtOffset(double offset)
    {
        int low = 0, high = _rowTops.Length - 1;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (_rowTops[mid] <= offset) low = mid; else high = mid - 1;
        }
        return low;
    }
    public double RowTopOf(int row) => row >= 0 && row < _rowTops.Length ? _rowTops[row] : 0;
    public double RowHeightOf(int row) => row >= 0 && row < _rowHeights.Length ? _rowHeights[row] : RowHeight;
    public int FirstVisibleRow => _rowTops.Length == 0 ? 0 : RowAtOffset(_verticalOffset);
    public int LastVisibleRow => _rowTops.Length == 0 ? 0 : RowAtOffset(_verticalOffset + Math.Max(0, _viewport.Height - 1));
    public void Zoom(double size)
    {
        double first = _verticalOffset / RowHeight;
        _fontSize = Math.Clamp(size, 10, 24); _measuredCharWidth = 0;
        SetRows(_rows, false);
        SetVerticalOffset(first * RowHeight);
    }
    public void SelectRow(int row, bool reveal = true)
    {
        _anchor = _selected = Math.Clamp(row, 0, Math.Max(0, _rows.Count - 1));
        if (Buffer is not null) SetCaret(OffsetForRow(_selected), reveal: false);
        if (reveal)
        {
            double top = RowTopOf(_selected);
            double height = RowHeightOf(_selected);
            if (top < _verticalOffset || top + height > _verticalOffset + _viewport.Height)
                SetVerticalOffset(Math.Max(0, top - _viewport.Height / 3));
        }
        InvalidateVisual();
    }
    public string SelectedText()
    {
        if (Buffer is not null && _columnSelection is { } column)
        {
            var lines = new List<string>();
            foreach (var (line, start, end) in column.OrderBy(s => s.Line))
            {
                if (line >= _documentLines.Count) continue;
                string text = _documentLines[line].Text;
                lines.Add(text[Math.Clamp(start, 0, text.Length)..Math.Clamp(end, 0, text.Length)]);
            }
            return string.Join(Buffer.NewLine, lines);
        }
        if (Buffer is not null) return Buffer.Text[Math.Min(_caret, _selectionAnchor)..Math.Max(_caret, _selectionAnchor)];
        if (_selected < 0 || _anchor < 0) return "";
        var result = new StringBuilder();
        for (int i = Math.Min(_anchor, _selected); i <= Math.Max(_anchor, _selected) && i < _rows.Count; i++)
        {
            var cell = IsLeft ? _rows[i].Left : _rows[i].Right;
            if (cell is null) continue;
            result.Append(cell.Text);
            result.Append(cell.Ending switch { "CRLF" => "\r\n", "LF" => "\n", "CR" => "\r", _ => "" });
        }
        return result.ToString();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        if (_rows.Count == 0) { DrawFinalCaretRow(dc); dc.DrawLine(new Pen(Border, 1), new(54, 0), new(54, RenderSize.Height)); return; }
        int first = FirstVisibleRow;
        for (int i = first; i < _rows.Count; i++)
        {
            double y = _rowTops[i] - _verticalOffset;
            if (y > RenderSize.Height) break;
            double height = _rowHeights[i];
            var row = _rows[i]; var cell = IsLeft ? row.Left : row.Right;
            var other = IsLeft ? row.Right : row.Left;
            var rect = new Rect(0, y, RenderSize.Width, height);
            if (row.HiddenLines > 0)
            {
                dc.DrawRectangle(Ghost, null, rect);
                dc.DrawText(Format($"  ⋯  已折叠 {row.HiddenLines:N0} 行相同内容 · 双击展开", Muted, 12), new(55, y + 3));
                continue;
            }
            var background = cell is null ? Ghost : !row.IsDifferent ? Brushes.White : other is not null ? Modified : IsLeft ? Deleted : Inserted;
            dc.DrawRectangle(background, null, rect);
            if (cell is null)
            {
                dc.PushClip(new RectangleGeometry(rect));
                var hatch = new Pen(Brush("#C4CCD8"), .8);
                for (double x = 54; x < RenderSize.Width + RowHeight; x += 9) dc.DrawLine(hatch, new(x, y), new(x - RowHeight, y + RowHeight));
                dc.Pop();
            }
            if (Buffer is null && _selected >= 0 && i >= Math.Min(_anchor, _selected) && i <= Math.Max(_anchor, _selected))
                dc.DrawRectangle(Selected, null, rect);
            // The fixed number gutter does not move during horizontal scrolling.
            dc.DrawRectangle(Brush("#F7F8FA"), null, new Rect(0, y, 54, height));
            if (cell is null) continue;
            var number = Format(cell.LineNumber.ToString(CultureInfo.InvariantCulture), Muted, 11);
            dc.DrawText(number, new(46 - number.Width, y + 4));
            if (row.IsDifferent) dc.DrawRectangle(other is not null ? Brush("#EAA05B") : IsLeft ? Brush("#D76565") : Brush("#65A67F"), null, new Rect(0, y, 3, height));
            if (_wrapLines)
            {
                dc.PushClip(new RectangleGeometry(new Rect(55, Math.Max(0, y), Math.Max(0, RenderSize.Width - 55), Math.Max(0, Math.Min(height, RenderSize.Height - y)))));
                DrawWrappedRow(dc, row, cell, other, y, height);
                dc.Pop();
            }
            else
            {
                dc.PushClip(new RectangleGeometry(new Rect(55, Math.Max(0, y), Math.Max(0, RenderSize.Width - 55), Math.Max(0, Math.Min(height, RenderSize.Height - y)))));
                DrawCell(dc, cell, new Point(64 - _horizontalOffset, y + 2), row.IsDifferent,
                    row.IsDifferent && other?.Text == cell.Text && other.Ending != cell.Ending);
                dc.Pop();
            }
        }
        DrawFinalCaretRow(dc);
        dc.DrawLine(new Pen(Border, 1), new(54, 0), new(54, RenderSize.Height));
    }

    /// <summary>Wrapped rendering: slices the expanded text into visual lines of WrapColumns columns.
    /// Monospace widths make column arithmetic exact; highlights, selection and caret are clipped
    /// to the visible slice. EOL markers render after the last visual line's text.</summary>
    private void DrawWrappedRow(DrawingContext dc, AlignedRow row, TextCell cell, TextCell? other, double y, double height)
    {
        (string expanded, int[] positions) = Expand(cell);
        int columns = positions[^1];
        int visualLines = Math.Max(1, (columns + WrapColumns - 1) / WrapColumns);
        bool endingDifference = row.IsDifferent && other?.Text == cell.Text && other.Ending != cell.Ending;
        for (int v = 0; v < visualLines; v++)
        {
            double lineY = y + v * RowHeight;
            if (lineY + RowHeight < 0) continue;
            int colStart = v * WrapColumns, colEnd = Math.Min(columns, colStart + WrapColumns);
            int charStart = CharAtColumn(positions, colStart), charEnd = CharAtColumn(positions, colEnd);
            string slice = expanded[charStart..charEnd];
            double textX = 64;
            var formatted = Format(slice, Ink);
            try
            {
                foreach (Match match in Syntax.Matches(slice))
                    formatted.SetForegroundBrush(match.Value.StartsWith("//", StringComparison.Ordinal) ? Brush("#6A8473")
                        : match.Value.StartsWith('"') ? Brush("#98632C") : Brush("#2E60AA"), match.Index, match.Length);
            }
            catch (RegexMatchTimeoutException) { /* Highlighting is optional; content still renders. */ }
            foreach (var span in cell.Highlights)
            {
                int from = Math.Clamp(positions[Math.Clamp(span.Start, 0, cell.Text.Length)], colStart, colEnd);
                int to = Math.Clamp(positions[Math.Clamp(span.Start + span.Length, 0, cell.Text.Length)], colStart, colEnd);
                if (to <= from) continue;
                var geometry = formatted.BuildHighlightGeometry(new(textX, lineY + 2), from - colStart, to - from);
                if (geometry is not null) dc.DrawGeometry(ChangedBackground, null, geometry);
                formatted.SetForegroundBrush(ChangedText, from - colStart, to - from);
            }
            DrawWrappedOverlay(dc, cell, formatted, positions, new(textX, lineY + 2), colStart, colEnd);
            dc.DrawText(formatted, new(textX, lineY + 2));
            if (v == visualLines - 1 && (ShowEndings || endingDifference))
            {
                var ending = endingDifference ? "  [" + cell.Ending + "]" : cell.Ending == "EOF" ? "  ∅" : cell.Ending == "CRLF" ? "  ↵" : cell.Ending == "CR" ? "  ←" : "  ↓";
                dc.DrawText(Format(ending, row.IsDifferent ? ChangedText : Muted, 10), new(textX + formatted.WidthIncludingTrailingWhitespace, lineY + 2));
            }
        }
    }

    private static (string Expanded, int[] Positions) Expand(TextCell cell)
    {
        var expanded = new StringBuilder(); var positions = new int[cell.Text.Length + 1];
        for (int i = 0; i < cell.Text.Length; i++)
        {
            positions[i] = expanded.Length;
            if (cell.Text[i] == '\t') expanded.Append(' ', 4 - expanded.Length % 4); else expanded.Append(cell.Text[i]);
        }
        positions[^1] = expanded.Length;
        return (expanded.ToString(), positions);
    }
    private static int CharAtColumn(int[] positions, int column)
    {
        int index = Array.BinarySearch(positions, column);
        return index < 0 ? ~index - 1 : index;
    }

    private void DrawCell(DrawingContext dc, TextCell cell, Point origin, bool different, bool endingDifference)
    {
        // Expand tabs consistently and maintain original-index -> display-index mapping for highlights.
        var expanded = new StringBuilder(); var positions = new int[cell.Text.Length + 1];
        for (int i = 0; i < cell.Text.Length; i++)
        {
            positions[i] = expanded.Length;
            if (cell.Text[i] == '\t') expanded.Append(' ', 4 - expanded.Length % 4); else expanded.Append(cell.Text[i]);
        }
        positions[^1] = expanded.Length;
        string text = expanded.ToString();
        // Very long lines render a viewport-sized substring rather than laying out megabytes of text.
        if (text.Length > 16_384)
        {
            int skip = Math.Max(0, (int)(_horizontalOffset / (_fontSize * .6)) - 20);
            skip = Math.Min(skip, text.Length); int count = Math.Min(16_384, text.Length - skip);
            dc.DrawText(Format(text.Substring(skip, count), Ink), new(origin.X + skip * _fontSize * .6, origin.Y));
            return;
        }
        var formatted = Format(text, Ink);
        try
        {
            foreach (Match match in Syntax.Matches(text))
                formatted.SetForegroundBrush(match.Value.StartsWith("//", StringComparison.Ordinal) ? Brush("#6A8473")
                    : match.Value.StartsWith('"') ? Brush("#98632C") : Brush("#2E60AA"), match.Index, match.Length);
        }
        catch (RegexMatchTimeoutException) { /* Highlighting is optional; content still renders. */ }
        foreach (var span in cell.Highlights)
        {
            int start = positions[Math.Clamp(span.Start, 0, cell.Text.Length)];
            int end = positions[Math.Clamp(span.Start + span.Length, 0, cell.Text.Length)];
            if (end <= start) continue;
            var geometry = formatted.BuildHighlightGeometry(origin, start, end - start);
            if (geometry is not null) dc.DrawGeometry(ChangedBackground, null, geometry);
            formatted.SetForegroundBrush(ChangedText, start, end - start);
        }
        DrawTextSelection(dc, cell, formatted, positions, origin);
        dc.DrawText(formatted, origin);
        DrawTextCaret(dc, cell, text, positions, origin);
        if (ShowEndings || endingDifference)
        {
            var ending = endingDifference ? "  [" + cell.Ending + "]" : cell.Ending == "EOF" ? "  ∅" : cell.Ending == "CRLF" ? "  ↵" : cell.Ending == "CR" ? "  ←" : "  ↓";
            dc.DrawText(Format(ending, different ? ChangedText : Muted, 10), new(origin.X + formatted.WidthIncludingTrailingWhitespace, origin.Y + 2));
        }
    }
    private FormattedText Format(string text, Brush brush, double? size = null) => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface, size ?? _fontSize, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    protected override Size MeasureOverride(Size availableSize) => new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 500 : availableSize.Height);
    protected override Size ArrangeOverride(Size finalSize)
    {
        bool resized = finalSize != _viewport;
        _viewport = finalSize;
        if (resized && _wrapLines) { LayoutRows(); InvalidateVisual(); }
        CoerceOffsets(); ScrollOwner?.InvalidateScrollInfo(); return finalSize;
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); Focus();
        if (Buffer is not null) { EditMouseDown(e); return; }
        int index = Math.Clamp((int)((e.GetPosition(this).Y + _verticalOffset) / RowHeight), 0, Math.Max(0, _rows.Count - 1));
        if (_rows.Count == 0) return;
        if (e.ClickCount == 2 && _rows[index].HiddenLines > 0) { ExpandContext?.Invoke(); e.Handled = true; return; }
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || _anchor < 0) _anchor = index;
        _selected = index; CaptureMouse(); RowSelected?.Invoke(index); InvalidateVisual(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Buffer is not null) { EditMouseMove(e); return; }
        if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed || _rows.Count == 0) return;
        double y = e.GetPosition(this).Y;
        if (y < 0) LineUp(); else if (y > _viewport.Height) LineDown();
        _selected = Math.Clamp((int)((y + _verticalOffset) / RowHeight), 0, _rows.Count - 1); InvalidateVisual();
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { ReleaseMouseCapture(); base.OnMouseLeftButtonUp(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Buffer is not null && EditKeyDown(e)) return;
        if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Clipboard.SetText(SelectedText()); e.Handled = true; }
        else if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { _anchor = 0; _selected = _rows.Count - 1; InvalidateVisual(); e.Handled = true; }
        else if (e.Key is Key.Up or Key.Down)
        { SelectRow(Math.Max(0, _selected) + (e.Key == Key.Up ? -1 : 1)); RowSelected?.Invoke(_selected); e.Handled = true; }
        else if (e.Key is Key.Home or Key.End && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        { SelectRow(e.Key == Key.Home ? 0 : _rows.Count - 1); e.Handled = true; }
        base.OnKeyDown(e);
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new PanePeer(this);
    private sealed class PanePeer(DiffPane owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override bool HasKeyboardFocusCore() => owner.IsKeyboardFocused;
        protected override bool IsKeyboardFocusableCore() => owner.Focusable;
        protected override void SetFocusCore() => owner.Focus();
        protected override string GetClassNameCore() => "DiffPane";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
        protected override string GetNameCore() => (owner.IsLeft ? "左侧代码" : "右侧代码") + (owner.Buffer is null ? "，只读" : "，可编辑");
    }
    public bool CanHorizontallyScroll { get; set; } = true;
    public bool CanVerticallyScroll { get; set; } = true;
    public double ExtentWidth => _wrapLines ? ViewportWidth : Math.Max(_extentWidth, ViewportWidth);
    public double ExtentHeight => _wrapLines
        ? (_rowTops.Length == 0 ? 0 : _rowTops[^1] + _rowHeights[^1]) + (HasFinalCaretRow ? RowHeight : 0)
        : (_rows.Count + (HasFinalCaretRow ? 1 : 0)) * RowHeight;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _horizontalOffset;
    public double VerticalOffset => _verticalOffset;
    public ScrollViewer? ScrollOwner { get; set; }
    private void CoerceOffsets()
    {
        _horizontalOffset = Math.Clamp(_horizontalOffset, 0, Math.Max(0, ExtentWidth - ViewportWidth));
        _verticalOffset = Math.Clamp(_verticalOffset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
    }
    public void SetHorizontalOffset(double offset)
    {
        if (double.IsNaN(offset)) return;
        _horizontalOffset = Math.Clamp(offset, 0, Math.Max(0, ExtentWidth - ViewportWidth));
        ScrollOwner?.InvalidateScrollInfo(); InvalidateVisual(); ViewChanged?.Invoke(this);
    }
    public void SetVerticalOffset(double offset)
    {
        if (double.IsNaN(offset)) return;
        _verticalOffset = Math.Clamp(offset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        ScrollOwner?.InvalidateScrollInfo(); InvalidateVisual(); ViewChanged?.Invoke(this);
    }
    public void LineUp() => SetVerticalOffset(_verticalOffset - RowHeight);
    public void LineDown() => SetVerticalOffset(_verticalOffset + RowHeight);
    public void LineLeft() => SetHorizontalOffset(_horizontalOffset - 30);
    public void LineRight() => SetHorizontalOffset(_horizontalOffset + 30);
    public void MouseWheelUp() => SetVerticalOffset(_verticalOffset - RowHeight * 3);
    public void MouseWheelDown() => SetVerticalOffset(_verticalOffset + RowHeight * 3);
    public void MouseWheelLeft() => LineLeft();
    public void MouseWheelRight() => LineRight();
    public void PageUp() => SetVerticalOffset(_verticalOffset - _viewport.Height + RowHeight);
    public void PageDown() => SetVerticalOffset(_verticalOffset + _viewport.Height - RowHeight);
    public void PageLeft() => SetHorizontalOffset(_horizontalOffset - _viewport.Width);
    public void PageRight() => SetHorizontalOffset(_horizontalOffset + _viewport.Width);
    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;
}
