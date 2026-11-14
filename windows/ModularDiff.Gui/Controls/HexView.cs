using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ModularDiff.Contracts;

namespace ModularDiff.Gui.Controls;

/// <summary>Virtualized side-by-side hex dump with per-byte difference coloring and inline editing:
/// click to position the cursor, type hex digits to overwrite (left side), Ctrl+S to save,
/// Ctrl+G to jump to a specific offset. Both sides share one offset column.</summary>
public sealed class HexView : FrameworkElement, IScrollInfo
{
    private byte[] _left = [], _right = [], _originalLeft = [];
    private IReadOnlyList<ByteDiff> _segments = [];
    private double _horizontalOffset, _verticalOffset, _extentWidth = 100;
    private Size _viewport;
    private double _fontSize = 13;
    private static readonly Typeface Typeface = new("Consolas");

    // Editing state
    private int _cursorOffset = -1;          // byte offset (-1 = no cursor)
    private bool _cursorHighNibble = true;   // editing the high or low nibble
    private bool _isDirty;
    private string? _editablePath;

    public int Rows => (Math.Max(_left.Length, _right.Length) + 15) / 16;
    public double RowHeight => Math.Ceiling(_fontSize * 1.55);
    public bool HasContent => _left.Length > 0 || _right.Length > 0;
    public bool IsDirty => _isDirty;
    public bool IsEditing => _editablePath is not null;
    public int CursorOffset => _cursorOffset;
    public string EditablePath => _editablePath ?? "";

    public event Action? DirtyStateChanged;

    public void SetContent(byte[] left, byte[] right, IReadOnlyList<ByteDiff> segments, string? editablePath = null)
    {
        _left = left; _right = right; _segments = segments;
        _originalLeft = [.. left];
        _editablePath = editablePath;
        _isDirty = false;
        _cursorOffset = -1;
        Focusable = editablePath is not null;   // only focusable when editing is enabled
        Cursor = editablePath is not null ? Cursors.IBeam : Cursors.Arrow;
        int columns = 9 + 16 * 3 + 17 + 16 * 3 + 17;
        _extentWidth = 40 + columns * _fontSize * .62;
        CoerceOffsets(); InvalidateMeasure(); InvalidateVisual(); ScrollOwner?.InvalidateScrollInfo();
    }

    public void GoToOffset(long offset)
    {
        if (offset < 0 || offset >= _left.Length) return;
        _cursorOffset = (int)offset;
        _cursorHighNibble = true;
        Focus();
        int row = (int)(offset / 16);
        double target = row * RowHeight;
        if (target < _verticalOffset || target + RowHeight > _verticalOffset + _viewport.Height)
            SetVerticalOffset(Math.Max(0, target - _viewport.Height / 3));
        InvalidateVisual();
    }

    public bool Save()
    {
        if (!_isDirty || _editablePath is null) return true;
        try
        {
            File.WriteAllBytes(_editablePath, _left);
            _originalLeft = [.. _left];
            _isDirty = false;
            DirtyStateChanged?.Invoke();
            InvalidateVisual();
            return true;
        }
        catch { return false; }
    }

    /// <summary>Exports the binary differences as a human-readable patch report.</summary>
    public string ExportPatch()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("ModularDiff Binary Patch Report");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        foreach (var segment in _segments)
        {
            sb.AppendLine($"Offset 0x{segment.Offset:X8} ({segment.Offset}): {segment.Length} bytes differ");
            for (long i = segment.Offset; i < segment.Offset + segment.Length && i < _left.Length && i < _right.Length; i++)
            {
                byte a = _left[(int)i], b = _right[(int)i];
                if (a != b)
                {
                    char ca = a is >= 32 and < 128 ? (char)a : '.';
                    char cb = b is >= 32 and < 128 ? (char)b : '.';
                    sb.AppendLine($"  0x{i:X8}: {a:X2} ('{ca}') → {b:X2} ('{cb}')");
                }
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private bool Differs(long offset)
    {
        int lo = 0, hi = _segments.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_segments[mid].Offset + _segments[mid].Length <= offset) lo = mid + 1; else hi = mid;
        }
        return lo < _segments.Count && offset >= _segments[lo].Offset;
    }

    // ─── rendering ───

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        if (!HasContent) return;
        var ink = new Brush[] { Brushes.Black, (Brush)new BrushConverter().ConvertFromString("#A52D22")! };
        var muted = (Brush)new BrushConverter().ConvertFromString("#8793A4")!;
        var cursorBrush = (Brush)new BrushConverter().ConvertFromString("#254E86")!;
        double x = 40 - _horizontalOffset;
        int first = (int)(_verticalOffset / RowHeight);
        int last = Math.Min(Rows, first + (int)(_viewport.Height / RowHeight) + 2);
        for (int row = first; row < last; row++)
        {
            double y = row * RowHeight - _verticalOffset;
            long baseOffset = (long)row * 16;
            dc.DrawText(Text(baseOffset.ToString("X8"), muted), new(x, y + 2));
            DrawSide(dc, _left, baseOffset, x + 90, y, ink, cursorBrush, true);
            DrawSide(dc, _right, baseOffset, x + 90 + (16 * 3 + 17) * _fontSize * .62, y, ink, cursorBrush, false);
        }
        var border = (Brush)new BrushConverter().ConvertFromString("#DEE3EA")!;
        dc.DrawLine(new Pen(border, 1), new(0, 0), new(0, RenderSize.Height));
    }

    private void DrawSide(DrawingContext dc, byte[] data, long baseOffset, double x, double y, Brush[] ink, Brush cursorBrush, bool isEditSide)
    {
        double charW = _fontSize * .62;
        double hexX = x, asciiX = x + 16 * 3 * charW + 8;
        for (int i = 0; i < 16; i++)
        {
            long at = baseOffset + i;
            bool inside = at < data.Length;
            bool differs = inside && Differs(at);
            bool isCursor = isEditSide && at == _cursorOffset;
            if (inside)
                dc.DrawText(Text(data[at].ToString("X2"), differs ? ink[1] : ink[0]), new(hexX, y + 2));
            // Cursor: draw a thin box around the nibble being edited
            if (isCursor)
            {
                double cursorX = _cursorHighNibble ? hexX : hexX + charW;
                dc.DrawRectangle(null, new Pen(cursorBrush, 1.5), new Rect(cursorX, y + 1, charW, RowHeight - 2));
            }
            dc.DrawText(Text(inside && data[at] is >= 32 and < 128 ? ((char)data[at]).ToString() : inside ? "." : " ", differs ? ink[1] : ink[0]),
                new(asciiX, y + 2));
            hexX += 3 * charW;
            asciiX += charW;
        }
    }

    private FormattedText Text(string text, Brush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface, _fontSize, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    // ─── keyboard/mouse editing ───

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsEditing) return;
        Focus();
        var pos = e.GetPosition(this);
        // Determine which side (left/right of the split)
        double splitX = 40 + 90 + (16 * 3 + 17) * _fontSize * .62 - _horizontalOffset;
        bool isLeft = pos.X < splitX;
        if (!isLeft) return;   // cursor only on the left (editable) side
        // Determine column within the hex area
        double hexStart = 40 + 90 - _horizontalOffset;
        double charW = _fontSize * .62;
        int col = (int)((pos.X - hexStart) / charW);
        int byteIndex = col / 3;   // 3 chars per byte (2 hex + 1 space)
        int nibbleInByte = col % 3;
        if (byteIndex < 0 || byteIndex > 15) return;
        int nibble = nibbleInByte == 0 ? 0 : nibbleInByte == 1 ? 1 : -1;   // -1 = space
        if (nibble < 0) return;
        int row = (int)((pos.Y + _verticalOffset) / RowHeight);
        long offset = (long)row * 16 + byteIndex;
        if (offset >= _left.Length) return;
        _cursorOffset = (int)offset;
        _cursorHighNibble = nibble == 0;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!IsEditing || _cursorOffset < 0) return;
        int current = _cursorOffset;
        switch (e.Key)
        {
            case Key.Left:
                e.Handled = true;
                if (_cursorHighNibble) { _cursorHighNibble = false; }
                else { _cursorHighNibble = true; if (current > 0) _cursorOffset = current - 1; }
                break;
            case Key.Right:
                e.Handled = true;
                if (!_cursorHighNibble) { _cursorHighNibble = true; }
                else { _cursorHighNibble = false; if (current < _left.Length - 1) _cursorOffset = current + 1; }
                break;
            case Key.Up:
                e.Handled = true;
                if (current >= 16) _cursorOffset = current - 16;
                break;
            case Key.Down:
                e.Handled = true;
                if (current + 16 < _left.Length) _cursorOffset = current + 16;
                break;
            case Key.Home:
                e.Handled = true;
                _cursorOffset = (current / 16) * 16; _cursorHighNibble = true;
                break;
            case Key.End:
                e.Handled = true;
                int lastInRow = Math.Min((current / 16) * 16 + 15, _left.Length - 1);
                _cursorOffset = lastInRow; _cursorHighNibble = false;
                break;
            case >= Key.D0 and <= Key.D9:
            case >= Key.A and <= Key.F:
                e.Handled = true;
                int digit = e.Key is >= Key.D0 and <= Key.D9 ? e.Key - Key.D0 : e.Key - Key.A + 10;
                OverwriteNibble(digit);
                break;
            case >= Key.NumPad0 and <= Key.NumPad9:
                e.Handled = true;
                OverwriteNibble(e.Key - Key.NumPad0);
                break;
        }
        InvalidateVisual();
    }

    private void OverwriteNibble(int digit)
    {
        if (_cursorOffset < 0 || _cursorOffset >= _left.Length) return;
        byte original = _left[_cursorOffset];
        byte modified = _cursorHighNibble
            ? (byte)((original & 0x0F) | (digit << 4))
            : (byte)((original & 0xF0) | digit);
        if (modified == original && _left[_cursorOffset] == _originalLeft[_cursorOffset]) { AdvanceCursor(); return; }
        _left[_cursorOffset] = modified;
        if (!_isDirty)
        {
            _isDirty = true;
            DirtyStateChanged?.Invoke();
        }
        AdvanceCursor();
    }

    private void AdvanceCursor()
    {
        if (!_cursorHighNibble) _cursorHighNibble = true;
        else if (_cursorOffset < _left.Length - 1) { _cursorOffset++; _cursorHighNibble = true; }
        // Auto-scroll if cursor moves out of view
        double cursorY = (_cursorOffset / 16) * RowHeight;
        if (cursorY < _verticalOffset) SetVerticalOffset(cursorY);
        else if (cursorY + RowHeight > _verticalOffset + _viewport.Height) SetVerticalOffset(cursorY + RowHeight - _viewport.Height);
    }

    // ─── IScrollInfo ───

    protected override Size MeasureOverride(Size availableSize) => new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 500 : availableSize.Height);
    protected override Size ArrangeOverride(Size finalSize) { _viewport = finalSize; CoerceOffsets(); ScrollOwner?.InvalidateScrollInfo(); return finalSize; }

    public bool CanHorizontallyScroll { get; set; } = true;
    public bool CanVerticallyScroll { get; set; } = true;
    public double ExtentWidth => Math.Max(_extentWidth, ViewportWidth);
    public double ExtentHeight => Rows * RowHeight;
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
    public void SetHorizontalOffset(double offset) { if (double.IsNaN(offset)) return; _horizontalOffset = Math.Clamp(offset, 0, Math.Max(0, ExtentWidth - ViewportWidth)); ScrollOwner?.InvalidateScrollInfo(); InvalidateVisual(); }
    public void SetVerticalOffset(double offset) { if (double.IsNaN(offset)) return; _verticalOffset = Math.Clamp(offset, 0, Math.Max(0, ExtentHeight - ViewportHeight)); ScrollOwner?.InvalidateScrollInfo(); InvalidateVisual(); }
    public void LineUp() => SetVerticalOffset(_verticalOffset - RowHeight);
    public void LineDown() => SetVerticalOffset(_verticalOffset + RowHeight);
    public void LineLeft() => SetHorizontalOffset(_horizontalOffset - 48);
    public void LineRight() => SetHorizontalOffset(_horizontalOffset + 48);
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
