using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ModularDiff.Contracts;

namespace ModularDiff.Gui.Controls;

/// <summary>Virtualized side-by-side hex dump with per-byte difference coloring;
/// both sides share one offset column, like Beyond Compare's hex view.</summary>
public sealed class HexView : FrameworkElement, IScrollInfo
{
    private byte[] _left = [], _right = [];
    private IReadOnlyList<ByteDiff> _segments = [];
    private double _horizontalOffset, _verticalOffset, _extentWidth = 100;
    private Size _viewport;
    private double _fontSize = 13;
    private static readonly Typeface Typeface = new("Consolas");

    public int Rows => (Math.Max(_left.Length, _right.Length) + 15) / 16;
    public double RowHeight => Math.Ceiling(_fontSize * 1.55);
    public bool HasContent => _left.Length > 0 || _right.Length > 0;

    public void SetContent(byte[] left, byte[] right, IReadOnlyList<ByteDiff> segments)
    {
        _left = left; _right = right; _segments = segments;
        int columns = 9 + 16 * 3 + 17 + 16 * 3 + 17;   // offset + two hex/ascii blocks
        _extentWidth = 40 + columns * _fontSize * .62;
        CoerceOffsets(); InvalidateMeasure(); InvalidateVisual(); ScrollOwner?.InvalidateScrollInfo();
    }

    private bool Differs(long offset)
    {
        // Segments are sorted; binary search for one covering the offset.
        int lo = 0, hi = _segments.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_segments[mid].Offset + _segments[mid].Length <= offset) lo = mid + 1; else hi = mid;
        }
        return lo < _segments.Count && offset >= _segments[lo].Offset;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        if (!HasContent) return;
        var ink = new Brush[] { Brushes.Black, (Brush)new BrushConverter().ConvertFromString("#A52D22")! };
        var muted = (Brush)new BrushConverter().ConvertFromString("#8793A4")!;
        double x = 40 - _horizontalOffset;
        int first = (int)(_verticalOffset / RowHeight);
        int last = Math.Min(Rows, first + (int)(_viewport.Height / RowHeight) + 2);
        for (int row = first; row < last; row++)
        {
            double y = row * RowHeight - _verticalOffset;
            long baseOffset = (long)row * 16;
            dc.DrawText(Text(baseOffset.ToString("X8"), muted), new(x, y + 2));
            DrawSide(dc, _left, baseOffset, x + 90, y, ink);
            DrawSide(dc, _right, baseOffset, x + 90 + (16 * 3 + 17) * _fontSize * .62, y, ink);
        }
        var border = (Brush)new BrushConverter().ConvertFromString("#DEE3EA")!;
        dc.DrawLine(new Pen(border, 1), new(0, 0), new(0, RenderSize.Height));
    }

    private void DrawSide(DrawingContext dc, byte[] data, long baseOffset, double x, double y, Brush[] ink)
    {
        double hexX = x, asciiX = x + 16 * 3 * _fontSize * .62 + 8;
        for (int i = 0; i < 16; i++)
        {
            long at = baseOffset + i;
            bool inside = at < data.Length;
            bool differs = inside && Differs(at);
            if (inside)
                dc.DrawText(Text(data[at].ToString("X2"), differs ? ink[1] : ink[0]), new(hexX, y + 2));
            dc.DrawText(Text(inside && !char.IsControl((char)data[at]) && data[at] < 128 ? ((char)data[at]).ToString() : inside ? "." : " ", differs ? ink[1] : ink[0]),
                new(asciiX, y + 2));
            hexX += 3 * _fontSize * .62;
            asciiX += _fontSize * .62;
        }
    }

    private FormattedText Text(string text, Brush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface, _fontSize, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

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
