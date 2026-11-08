using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ModularDiff.Contracts;

namespace ModularDiff.Gui.Controls;

public sealed class DiffOverview : FrameworkElement
{
    private TextPresentation? _presentation;
    private int _first, _last;
    public event Action<int>? NavigateRequested;
    public DiffOverview() { Cursor = Cursors.Hand; ToolTip = "点击概览跳转到对应位置"; }
    public void SetPresentation(TextPresentation? presentation) { _presentation = presentation; InvalidateVisual(); }
    public void SetViewport(int first, int last) { _first = first; _last = last; InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(239, 242, 247)), null, new Rect(RenderSize));
        if (_presentation is null || _presentation.TotalRows == 0) return;
        double scale = ActualHeight / _presentation.TotalRows;
        foreach (var hunk in _presentation.Hunks)
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(220, 122, 91)), null,
                new Rect(3, hunk.StartRow * scale, Math.Max(0, ActualWidth - 6), Math.Max(3, (hunk.EndRow - hunk.StartRow + 1) * scale)));
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(30, 53, 105, 181)), new Pen(new SolidColorBrush(Color.FromRgb(90, 125, 170)), 1),
            new Rect(1, _first * scale, Math.Max(0, ActualWidth - 2), Math.Max(5, Math.Min(ActualHeight - _first * scale, (_last - _first + 1) * scale))));
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_presentation is not null && ActualHeight > 0)
            NavigateRequested?.Invoke(Math.Clamp((int)(e.GetPosition(this).Y / ActualHeight * _presentation.TotalRows), 0, Math.Max(0, _presentation.TotalRows - 1)));
        e.Handled = true;
    }
}
