using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ModularDiff.Gui.Controls;

/// <summary>
/// The scrollable area of a <see cref="FolderList"/>: it owns the pooled rows, decides which
/// node each of them shows, and answers the ScrollViewer. Offsets are in pixels but always a
/// whole number of rows, like the item scrolling of a grid. Nothing here rebuilds a row: a
/// row is the row template loaded once, and a new row set or a scroll only switches its data
/// context, so its bindings refresh the text of the existing visuals.
/// </summary>
public sealed class FolderRowHost : FrameworkElement, IScrollInfo
{
    public static readonly DependencyProperty RowTemplateProperty = DependencyProperty.Register(
        nameof(RowTemplate), typeof(DataTemplate), typeof(FolderRowHost),
        new PropertyMetadata(null, (host, args) => ((FolderRowHost)host).DropRows()));

    public static readonly DependencyProperty RowHeightProperty = DependencyProperty.Register(
        nameof(RowHeight), typeof(double), typeof(FolderRowHost),
        new FrameworkPropertyMetadata(27.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Width below which the rows scroll sideways: the sum of the columns' minimum widths.</summary>
    public static readonly DependencyProperty RowMinWidthProperty = DependencyProperty.Register(
        nameof(RowMinWidth), typeof(double), typeof(FolderRowHost),
        new FrameworkPropertyMetadata(426.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private const double Line = 16;   // one "line" of sideways scrolling, as in the stock ScrollViewer

    /// <summary>Set on a row that belongs to the selection.</summary>
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.RegisterAttached(
        "IsSelected", typeof(bool), typeof(FolderRowHost), new FrameworkPropertyMetadata(false));

    /// <summary>Set on the row the keyboard acts on while the list has the focus.</summary>
    public static readonly DependencyProperty IsCurrentProperty = DependencyProperty.RegisterAttached(
        "IsCurrent", typeof(bool), typeof(FolderRowHost), new FrameworkPropertyMetadata(false));

    public static bool GetIsSelected(DependencyObject row) => (bool)row.GetValue(IsSelectedProperty);
    public static void SetIsSelected(DependencyObject row, bool value) => row.SetValue(IsSelectedProperty, value);
    public static bool GetIsCurrent(DependencyObject row) => (bool)row.GetValue(IsCurrentProperty);
    public static void SetIsCurrent(DependencyObject row, bool value) => row.SetValue(IsCurrentProperty, value);

    private readonly List<FrameworkElement> _rows = [];
    private Size _viewport;
    private double _availableHeight;
    private Point _offset;
    private ScrollViewer? _owner;

    public DataTemplate? RowTemplate { get => (DataTemplate?)GetValue(RowTemplateProperty); set => SetValue(RowTemplateProperty, value); }
    public double RowHeight { get => (double)GetValue(RowHeightProperty); set => SetValue(RowHeightProperty, value); }
    public double RowMinWidth { get => (double)GetValue(RowMinWidthProperty); set => SetValue(RowMinWidthProperty, value); }

    /// <summary>The list whose nodes and selection the rows show.</summary>
    public FolderList? List { get; set; }

    /// <summary>Index of the node shown by the first pooled row.</summary>
    public int FirstRow => (int)(_offset.Y / RowHeight);
    /// <summary>How many rows the pool holds — one per row the screen can show.</summary>
    public int RowCount => _rows.Count;

    public bool CanHorizontallyScroll { get; set; } = true;
    public bool CanVerticallyScroll { get; set; } = true;
    public double ExtentWidth => Math.Max(RowMinWidth, _viewport.Width);
    public double ExtentHeight => Count * RowHeight;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get => _owner; set => _owner = value; }

    private int Count => List?.Rows.Count ?? 0;

    /// <summary>Rows that fill the viewport.</summary>
    private int RowsThatFit => Math.Max(1, (int)Math.Ceiling(_availableHeight / RowHeight - 1e-9));

    /// <summary>The last row that can be the first on screen.</summary>
    private int LastFirstRow => Math.Max(0, Count - RowsThatFit);

    public void LineUp() => ScrollToRow(FirstRow - 1);
    public void LineDown() => ScrollToRow(FirstRow + 1);
    public void LineLeft() => SetHorizontalOffset(_offset.X - Line);
    public void LineRight() => SetHorizontalOffset(_offset.X + Line);
    public void PageUp() => ScrollToRow(FirstRow - RowsThatFit);
    public void PageDown() => ScrollToRow(FirstRow + RowsThatFit);
    public void PageLeft() => SetHorizontalOffset(_offset.X - _viewport.Width);
    public void PageRight() => SetHorizontalOffset(_offset.X + _viewport.Width);

    public void MouseWheelUp() => ScrollToRow(FirstRow - (int)WheelRows());
    public void MouseWheelDown() => ScrollToRow(FirstRow + (int)WheelRows());
    public void MouseWheelLeft() => SetHorizontalOffset(_offset.X - WheelRows() * Line);
    public void MouseWheelRight() => SetHorizontalOffset(_offset.X + WheelRows() * Line);

    public void SetHorizontalOffset(double offset)
    {
        if (double.IsNaN(offset)) return;
        Move(Clamp(offset, Math.Max(0, RowMinWidth - _viewport.Width)), _offset.Y);
    }

    /// <summary>Whole rows only — a dragged scrollbar or a page turn never leaves a row cut in two.
    /// The scroll viewer passes infinities for its own ScrollToTop / ScrollToBottom.</summary>
    public void SetVerticalOffset(double offset)
    {
        if (double.IsNaN(offset)) return;
        double rows = double.IsInfinity(offset) ? (offset > 0 ? Count : 0) : Math.Round(offset / RowHeight);
        Move(_offset.X, Clamp(rows, LastFirstRow) * RowHeight);
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (rectangle.IsEmpty || visual is null || visual == this || !IsAncestorOf(visual)) return Rect.Empty;
        Rect bounds = visual.TransformToAncestor(this).TransformBounds(rectangle);
        double y = _offset.Y, x = _offset.X;
        bool upward = bounds.Top < y, leftward = bounds.Left < x;
        if (upward) y = bounds.Top;
        else if (bounds.Bottom > y + _viewport.Height) y = bounds.Bottom - _viewport.Height;
        if (leftward) x = bounds.Left;
        else if (bounds.Right > x + _viewport.Width) x = bounds.Right - _viewport.Width;
        SetVerticalOffset((upward ? Math.Floor(y / RowHeight) : Math.Ceiling(y / RowHeight)) * RowHeight);
        SetHorizontalOffset(x);
        bounds.Intersect(new Rect(_offset, _viewport));   // the part now in view, in our coordinates
        return new Rect(bounds.X - _offset.X, bounds.Y - _offset.Y, bounds.Width, bounds.Height);
    }

    /// <summary>Scrolls just enough to show the whole row at <paramref name="index"/>.</summary>
    public void BringRowIntoView(int index)
    {
        if ((uint)index >= (uint)Count) return;
        if (index < FirstRow) ScrollToRow(index);
        else if (index >= FirstRow + RowsThatFit) ScrollToRow(index - RowsThatFit + 1);
    }

    public void ScrollToTop() => ScrollToRow(0);

    private void ScrollToRow(int row) => Move(_offset.X, Clamp(row, LastFirstRow) * RowHeight);

    /// <summary>Re-points every pooled row at its node, for texts the node computes on read.</summary>
    public void RefreshRows()
    {
        foreach (var row in _rows) { var node = row.DataContext; row.DataContext = null; row.DataContext = node; }
    }

    private void DropRows()
    {
        foreach (var row in _rows) RemoveVisualChild(row);
        _rows.Clear();
        InvalidateMeasure();
    }

    private double WheelRows()
    {
        int lines = SystemParameters.WheelScrollLines;
        return lines < 0 ? Math.Max(1, Math.Floor(_viewport.Height / RowHeight)) : lines;
    }

    private static double Clamp(double value, double maximum) => Math.Max(0, Math.Min(value, maximum));

    private void Move(double x, double y)
    {
        if (Math.Abs(x - _offset.X) < 0.01 && Math.Abs(y - _offset.Y) < 0.01) return;
        _offset = new Point(x, y);
        InvalidateMeasure();
        _owner?.InvalidateScrollInfo();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) || double.IsNaN(availableSize.Width) ? RowMinWidth : availableSize.Width;
        // Unconstrained (a design surface): show a screenful rather than the whole list.
        _availableHeight = double.IsInfinity(availableSize.Height) || double.IsNaN(availableSize.Height) ? RowHeight * 20 : availableSize.Height;
        _viewport = new Size(width, Math.Min(_availableHeight, Count * RowHeight));

        // The pool always holds a screenful, whatever the list holds now: the rows of a list that
        // just shrank — or of an empty one — are what the next keystroke re-uses.
        while (_rows.Count < RowsThatFit)
        {
            var row = (FrameworkElement)RowTemplate!.LoadContent();
            _rows.Add(row);
            AddVisualChild(row);
        }
        while (_rows.Count > RowsThatFit)
        {
            var row = _rows[^1];
            _rows.RemoveAt(_rows.Count - 1);
            RemoveVisualChild(row);
        }

        int first = Math.Min(FirstRow, LastFirstRow);   // the list may have shrunk under the offset
        _offset.Y = first * RowHeight;
        var list = List;
        var slot = new Size(Math.Max(width, RowMinWidth), RowHeight);
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            if (list is null || first + i >= list.Rows.Count)
                row.Visibility = Visibility.Collapsed;   // fewer rows than the screen holds
            else
            {
                var node = list.Rows[first + i];
                row.Visibility = Visibility.Visible;
                row.SetValue(IsSelectedProperty, list.IsSelected(node));
                row.SetValue(IsCurrentProperty, list.IsCurrent(node));
                if (!ReferenceEquals(row.DataContext, node)) row.DataContext = node;
            }
            row.Measure(slot);
        }
        _owner?.InvalidateScrollInfo();
        return _viewport;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        double width = Math.Max(_viewport.Width, RowMinWidth);
        for (int i = 0; i < _rows.Count; i++)
            _rows[i].Arrange(new Rect(-_offset.X, i * RowHeight, width, RowHeight));
        return arrangeSize;
    }

    protected override int VisualChildrenCount => _rows.Count;

    protected override Visual GetVisualChild(int index) => _rows[index];
}
