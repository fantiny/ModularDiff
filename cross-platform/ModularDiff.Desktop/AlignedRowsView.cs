using Avalonia.VisualTree;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ModularDiff.Contracts;

namespace ModularDiff.Desktop;

/// <summary>差异对齐自绘视图(移植 WPF DiffPane 思路):统一行高、只渲染视口、词级高亮、行背景与行号。
/// 渲染与输入分离 —— 点击文本区时向宿主请求就地编辑(宿主把 TextBox 覆盖到单元格上,输入法安全);
/// 行号列负责选择(单击/Shift 范围/Cmd 点选/左键拖动),右键交宿主构建换行/格式化菜单。</summary>
public sealed class AlignedRowsView : Control
{
    public const double RowHeight = 22;
    private const double NumberWidth = 46, TextSize = 13, MarkerWidth = 54;
    private static readonly FontFamily Mono = new("Cascadia Code,Menlo,DejaVu Sans Mono,monospace");
    private static readonly Typeface Typeface = new(Mono);
    private static readonly IBrush Number = new SolidColorBrush(Color.Parse("#8793A4"));
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#253041"));
    private static readonly IBrush Ghost = new SolidColorBrush(Color.Parse("#F2F4F7"));
    private static readonly IBrush Deleted = new SolidColorBrush(Color.Parse("#FFF0F0"));
    private static readonly IBrush Inserted = new SolidColorBrush(Color.Parse("#ECF7EF"));
    private static readonly IBrush Modified = new SolidColorBrush(Color.Parse("#FFF0E9"));
    private static readonly IBrush Selected = new SolidColorBrush(Color.Parse("#D9E8FF"));
    private static readonly IBrush Word = new SolidColorBrush(Color.Parse("#FFD4BC"));

    private IReadOnlyList<AlignedRow> _rows = [];
    private readonly HashSet<int> _selected = [];
    private int _primary = -1, _anchor = -1;
    private bool _dragging;

    public IReadOnlyList<AlignedRow> Rows { get => _rows; set { _rows = value; _selected.Clear(); _primary = _anchor = -1; InvalidateMeasure(); InvalidateVisual(); } }
    public int RowCount => _rows.Count;
    public IReadOnlyList<AlignedRow> SelectedRows => _selected.OrderBy(i => i).Select(i => _rows[i]).ToList();
    public int PrimarySelected => _primary;
    /// <summary>行文本区左键(请求就地编辑):(行, 作用左/右侧)。</summary>
    public event Action<AlignedRow, bool>? EditRequested;
    /// <summary>右键任意位置(宿主按行+侧构建换行等菜单,并调用 OpenMenu 挂到本控件)。</summary>
    public event Action<AlignedRow, bool>? MenuRequested;
    public event Action? SelectionChanged;

    public void SelectRows(IReadOnlyList<AlignedRow> rows)
    {
        _selected.Clear();
        foreach (var row in rows)
        {
            int index = IndexOf(row);
            if (index >= 0 && !_selected.Add(index)) { }
        }
        _primary = _selected.Count == 0 ? -1 : _selected.Min();
        _anchor = _primary;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }
    public void EnsureVisible(int index)
    {
        if (Scroller is not { } scroller || index < 0 || index >= _rows.Count) return;
        double top = index * RowHeight, bottom = top + RowHeight;
        if (top < scroller.Offset.Y) scroller.Offset = new(scroller.Offset.X, top);
        else if (bottom > scroller.Offset.Y + scroller.Viewport.Height)
            scroller.Offset = new(scroller.Offset.X, bottom - scroller.Viewport.Height);
    }
    public Rect CellRect(int index, bool left)
    {
        double half = Bounds.Width / 2;
        return new Rect(left ? NumberWidth : half + NumberWidth, index * RowHeight + 1, half - NumberWidth - MarkerWidth, RowHeight - 2);
    }
    /// <summary>宿主构建好菜单后挂到视图并打开(在指针位置弹出)。</summary>
    public void OpenMenu(ContextMenu menu) { ContextMenu = menu; menu.Open(this); }
    private ContextMenu? _menu;
    public new ContextMenu? ContextMenu { get => _menu; set { _menu = value; base.ContextMenu = value; } }
    private ScrollViewer? _scroller;

    protected override Size MeasureOverride(Size availableSize) => new(availableSize.Width, Math.Ceiling(_rows.Count * RowHeight));
    private ScrollViewer? Scroller
    {
        get
        {
            if (_scroller is not null) return _scroller;
            _scroller = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
            if (_scroller is not null) _scroller.ScrollChanged += (_, _) => InvalidateVisual();
            return _scroller;
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_rows.Count == 0) return;
        double half = Math.Max(80, Bounds.Width / 2);
        var scroller = Scroller;
        double top = scroller?.Offset.Y ?? 0;
        int first = Math.Max(0, (int)(top / RowHeight) - 1);
        int last = Math.Min(_rows.Count, (int)((top + (scroller?.Viewport.Height ?? Bounds.Height)) / RowHeight) + 2);
        for (int i = first; i < last; i++)
        {
            var row = _rows[i];
            double y = i * RowHeight;
            DrawSide(context, row, i, true, 0, half);
            DrawSide(context, row, i, false, half, half);
        }
    }
    private void DrawSide(DrawingContext context, AlignedRow row, int index, bool left, double origin, double width)
    {
        TextCell? cell = left ? row.Left : row.Right;
        double y = index * RowHeight;
        IBrush? tint = cell is null ? Ghost
            : row.IsDifferent ? (left ? (row.Right is null ? Deleted : Modified) : (row.Left is null ? Inserted : Modified))
            : null;
        if (_selected.Contains(index))
        {
            context.FillRectangle(Selected, new Rect(0, y, width * 2, RowHeight));
            if (tint is not null)
                using (context.PushOpacity(0.45))
                    context.FillRectangle(tint, new Rect(origin, y, width, RowHeight));   // 选中时差异色以 45% 叠加
        }
        else if (tint is not null) context.FillRectangle(tint, new Rect(origin, y, width, RowHeight));
        if (cell is null) return;
        var number = new FormattedText(cell.LineNumber.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, 11, Number);
        context.DrawText(number, new Point(origin + NumberWidth - 8 - number.Width, y + 4));
        // 性能关键:FormattedText 会对整行做排版,压缩文件的超长行(数万字符)会让每次重绘卡几秒。
        // 只对"视口内可见的前缀"排版与绘制,其余截断;词级高亮同样按可见前缀裁剪。
        double charWidth = CharWidth;
        int visibleChars = Math.Max(1, (int)((width - NumberWidth - 10) / charWidth) + 1);
        string drawn = cell.Text.Length > visibleChars ? cell.Text[..visibleChars] : cell.Text;
        double textX = origin + NumberWidth;
        foreach (var span in cell.Highlights)
        {
            int start = Math.Clamp(span.Start, 0, drawn.Length), length = Math.Clamp(span.Length, 0, drawn.Length - start);
            if (length <= 0) continue;
            context.FillRectangle(Word, new Rect(textX + start * charWidth, y + 2, length * charWidth, RowHeight - 5), 2);
        }
        var text = new FormattedText(drawn, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, TextSize, Ink);
        context.DrawText(text, new Point(textX, y + 3));
        if (cell.Text.Length > drawn.Length)
        {
            var more = new FormattedText("…", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, TextSize, Number);
            context.DrawText(more, new Point(textX + drawn.Length * charWidth, y + 3));
        }
        if (cell.Ending is "CRLF" or "CR")
        {
            var marker = new FormattedText(cell.Ending == "CRLF" ? "⏎CRLF" : "⏎CR", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, 10, Number);
            context.DrawText(marker, new Point(origin + width - MarkerWidth + 4, y + 5));
        }
    }
    private static double _charWidth;
    private static double CharWidth => _charWidth != 0 ? _charWidth : _charWidth = new FormattedText("M", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, TextSize, Ink).Width;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Point point = e.GetPosition(this);
        int index = (int)(point.Y / RowHeight);
        if (index < 0 || index >= _rows.Count) return;
        double half = Math.Max(80, Bounds.Width / 2);
        bool left = point.X < half;
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed)
        {
            if (!_selected.Contains(index)) { _selected.Clear(); _selected.Add(index); _primary = _anchor = index; InvalidateVisual(); SelectionChanged?.Invoke(); }
            MenuRequested?.Invoke(_rows[index], left);
            e.Handled = true;
            return;
        }
        if (!properties.IsLeftButtonPressed) return;
        if (point.X % half < NumberWidth)   // 行号列:单击/Shift 范围/Cmd 点选/拖动
        {
            ApplyClickSelection(index, e.KeyModifiers);
            _dragging = true;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        _selected.Clear(); _selected.Add(index); _primary = _anchor = index;
        InvalidateVisual(); SelectionChanged?.Invoke();
        EditRequested?.Invoke(_rows[index], left);
        e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        int index = Math.Clamp((int)(e.GetPosition(this).Y / RowHeight), 0, _rows.Count - 1);
        if (index != _primary)
        {
            _selected.Clear();
            for (int i = Math.Min(_anchor, index); i <= Math.Max(_anchor, index); i++) _selected.Add(i);
            _primary = index;
            InvalidateVisual(); SelectionChanged?.Invoke();
        }
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging) { _dragging = false; e.Pointer.Capture(null); e.Handled = true; }
    }
    private void ApplyClickSelection(int index, KeyModifiers modifiers)
    {
        bool toggle = modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta);
        if (modifiers.HasFlag(KeyModifiers.Shift) && _anchor >= 0)
        {
            _selected.Clear();
            for (int i = Math.Min(_anchor, index); i <= Math.Max(_anchor, index); i++) _selected.Add(i);
        }
        else if (toggle)
        {
            if (!_selected.Remove(index)) _selected.Add(index);
            _anchor = index;
        }
        else { _selected.Clear(); _selected.Add(index); _anchor = index; }
        _primary = index;
        InvalidateVisual(); SelectionChanged?.Invoke();
    }
    public int IndexOfRow(AlignedRow row) => IndexOf(row);
    private int IndexOf(AlignedRow row)
    {
        for (int i = 0; i < _rows.Count; i++) if (ReferenceEquals(_rows[i], row)) return i;
        return -1;
    }
}
