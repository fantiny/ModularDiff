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
    private static readonly IBrush TextSelect = new SolidColorBrush(Color.FromArgb(0x80, 0x33, 0x99, 0xFF));

    private IReadOnlyList<AlignedRow> _rows = [];
    private (int Row, int Column, bool Left)? _textAnchor, _textHead;
    private bool _textSelecting, _pressPending;
    private readonly HashSet<int> _selected = [];
    private int _primary = -1, _anchor = -1;
    private bool _dragging;
    private bool _vertical;
    private readonly Dictionary<int, int> _bookmarks = new();

    public AlignedRowsView() => Focusable = true;
    /// <summary>上下布局(true=左右两侧改为上下两栏);渲染与命中统一走 Band 几何。</summary>
    public bool Vertical { get => _vertical; set { if (_vertical == value) return; _vertical = value; InvalidateMeasure(); InvalidateVisual(); } }
    private Rect Band(int row, bool left)
    {
        if (_vertical) return new Rect(0, (row * 2 + (left ? 0 : 1)) * RowHeight, Bounds.Width, RowHeight);
        double half = Math.Max(80, Bounds.Width / 2);
        return new Rect(left ? 0 : half, row * RowHeight, half, RowHeight);
    }
    private (int Row, bool Left)? BandAt(Point point)
    {
        if (_vertical)
        {
            int slot = (int)(point.Y / RowHeight);
            if (slot < 0 || slot / 2 >= _rows.Count) return null;
            return (slot / 2, slot % 2 == 1);
        }
        int row = (int)(point.Y / RowHeight);
        if (row < 0 || row >= _rows.Count) return null;
        return (row, point.X >= Math.Max(80, Bounds.Width / 2));
    }
    public IReadOnlyList<AlignedRow> Rows { get => _rows; set { _rows = value; _selected.Clear(); _primary = _anchor = -1; _textAnchor = _textHead = null; InvalidateMeasure(); InvalidateVisual(); } }
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
        Rect band = Band(index, left);
        return new Rect(band.X + NumberWidth, band.Y, band.Width - NumberWidth, RowHeight);
    }
    private static readonly IBrush BookmarkBrush = new SolidColorBrush(Color.Parse("#8AB4FF"));
    private void DrawBookmark(DrawingContext context, int index, int digit)
    {
        Rect band = Band(index, true);
        var rect = new Rect(band.X + 1, band.Y + 3, 14, RowHeight - 6);
        context.FillRectangle(BookmarkBrush, rect, 3);
        var label = new FormattedText(digit.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, 9, Brushes.White);
        context.DrawText(label, new Point(rect.X + (rect.Width - label.Width) / 2, rect.Y + 2));
    }
    /// <summary>设置/清除书签(⌘⇧数字);行索引绑定,重对齐后语义上可能漂移。</summary>
    public void ToggleBookmark(int row, int digit)
    {
        if (row < 0 || row >= _rows.Count || digit is < 1 or > 9) return;
        if (!_bookmarks.Remove(row)) _bookmarks[row] = digit;
        InvalidateVisual();
    }
    /// <summary>跳到书签(⌘数字);命中则选中并滚动到该行。</summary>
    public bool GoToBookmark(int digit)
    {
        foreach (var (row, mark) in _bookmarks)
            if (mark == digit)
            {
                _selected.Clear(); _selected.Add(row); _primary = _anchor = row;
                EnsureVisible(row); InvalidateVisual(); SelectionChanged?.Invoke();
                return true;
            }
        return false;
    }
    /// <summary>选中当前行所在的整个差异块(所有同 HunkId 的行)。</summary>
    public void SelectHunk(AlignedRow row)
    {
        if (row.HunkId < 0) { SelectRows([row]); return; }
        SelectRows(_rows.Where(r => r.HunkId == row.HunkId).ToList());
    }
    /// <summary>当前跨行文本选区的行范围与侧;无选区为 null。</summary>
    public (int FromRow, int ToRow, bool Left)? TextSelectionRange
    {
        get
        {
            if (_textAnchor is not { } a || _textHead is not { } b || a.Left != b.Left) return null;
            return (Math.Min(a.Row, b.Row), Math.Max(a.Row, b.Row), a.Left);
        }
    }
    /// <summary>宿主构建好菜单后挂到视图并打开(在指针位置弹出)。</summary>
    public void OpenMenu(ContextMenu menu) { ContextMenu = menu; menu.Open(this); }
    private ContextMenu? _menu;
    public new ContextMenu? ContextMenu { get => _menu; set { _menu = value; base.ContextMenu = value; } }
    private ScrollViewer? _scroller;

    protected override Size MeasureOverride(Size availableSize) => new(availableSize.Width, Math.Ceiling(_rows.Count * RowHeight * (_vertical ? 2 : 1)));
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
        var scroller = Scroller;
        double top = scroller?.Offset.Y ?? 0;
        double divisor = RowHeight * (_vertical ? 2 : 1);
        int first = Math.Max(0, (int)(top / divisor) - 1);
        int last = Math.Min(_rows.Count, (int)((top + (scroller?.Viewport.Height ?? Bounds.Height)) / divisor) + 2);
        for (int i = first; i < last; i++)
        {
            var row = _rows[i];
            DrawSide(context, row, i, true);
            DrawSide(context, row, i, false);
            if (_bookmarks.TryGetValue(i, out int digit)) DrawBookmark(context, i, digit);
        }
        if (_textAnchor is { } anchor && _textHead is { } head && anchor.Left == head.Left)
        {
            var (from, to) = anchor.Row <= head.Row ? (anchor, head) : (head, anchor);
            for (int i = from.Row; i <= Math.Min(to.Row, _rows.Count - 1); i++)
            {
                Rect band = Band(i, from.Left);
                double textX = band.X + NumberWidth;
                double x0 = i == from.Row ? textX + from.Column * CharWidth : textX;
                double x1 = i == to.Row ? textX + to.Column * CharWidth : band.Right;
                context.FillRectangle(TextSelect, new Rect(Math.Min(x0, x1), band.Y + 1, Math.Max(2, Math.Abs(x1 - x0)), RowHeight - 2), 2);
            }
        }
    }
    private void DrawSide(DrawingContext context, AlignedRow row, int index, bool left)
    {
        TextCell? cell = left ? row.Left : row.Right;
        Rect band = Band(index, left);
        IBrush? tint = cell is null ? Ghost
            : row.IsDifferent ? (left ? (row.Right is null ? Deleted : Modified) : (row.Left is null ? Inserted : Modified))
            : null;
        if (_selected.Contains(index))
        {
            context.FillRectangle(Selected, band);
            if (tint is not null)
                using (context.PushOpacity(0.45))
                    context.FillRectangle(tint, band);   // 选中时差异色以 45% 叠加
        }
        else if (tint is not null) context.FillRectangle(tint, band);
        if (cell is null) return;
        var number = new FormattedText(cell.LineNumber.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, 11, Number);
        context.DrawText(number, new Point(band.X + NumberWidth - 8 - number.Width, band.Y + 4));
        // 性能关键:FormattedText 会对整行做排版,压缩文件的超长行(数万字符)会让每次重绘卡几秒。
        // 只对"视口内可见的前缀"排版与绘制,其余截断;词级高亮同样按可见前缀裁剪。
        double charWidth = CharWidth;
        int visibleChars = Math.Max(1, (int)((band.Width - NumberWidth - 10) / charWidth) + 1);
        string drawn = cell.Text.Length > visibleChars ? cell.Text[..visibleChars] : cell.Text;
        double textX = band.X + NumberWidth;
        foreach (var span in cell.Highlights)
        {
            int start = Math.Clamp(span.Start, 0, drawn.Length), length = Math.Clamp(span.Length, 0, drawn.Length - start);
            if (length <= 0) continue;
                context.FillRectangle(Word, new Rect(textX + start * charWidth, band.Y + 2, length * charWidth, RowHeight - 5), 2);
        }
        var text = new FormattedText(drawn, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, TextSize, Ink);
        context.DrawText(text, new Point(textX, band.Y + (RowHeight - TextSize * 1.35) / 2));   // 与居中编辑器的文本基线对齐
        if (cell.Text.Length > drawn.Length)
        {
            var more = new FormattedText("…", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, TextSize, Number);
            context.DrawText(more, new Point(textX + drawn.Length * charWidth, band.Y + 3));
        }
        if (cell.Ending is "CRLF" or "CR")
        {
            var marker = new FormattedText(cell.Ending == "CRLF" ? "⏎CRLF" : "⏎CR", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, 10, Number);
            context.DrawText(marker, new Point(band.Right - MarkerWidth + 4, band.Y + 5));
        }
    }
    private static double _charWidth;
    private static double CharWidth => _charWidth != 0 ? _charWidth : _charWidth = new FormattedText("M", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, TextSize, Ink).Width;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Point point = e.GetPosition(this);
        if (BandAt(point) is not { } hit) return;
        int index = hit.Row;
        bool left = hit.Left;
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed)
        {
            if (!_selected.Contains(index)) { _selected.Clear(); _selected.Add(index); _primary = _anchor = index; InvalidateVisual(); SelectionChanged?.Invoke(); }
            MenuRequested?.Invoke(_rows[index], left);
            e.Handled = true;
            return;
        }
        if (!properties.IsLeftButtonPressed) return;
        Focus();
        if (point.X - Band(index, left).X < NumberWidth)   // 行号列:单击/Shift 范围/Cmd 点选/拖动选择行
        {
            ClearTextSelection();
            ApplyClickSelection(index, e.KeyModifiers);
            _dragging = true;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        // 文本区:先按下待判 —— 松开前几乎未移动 = 点击(进编辑);拖动超过阈值 = 跨行文本选择。
        _pressPoint = e.GetPosition(this);
        _pressLocation = LocationAt(_pressPoint.Value, index, left);
        _pressPending = true;
        e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
        {
            int index = Math.Clamp((int)(e.GetPosition(this).Y / RowHeight), 0, _rows.Count - 1);
            if (index != _primary)
            {
                _selected.Clear();
                for (int i = Math.Min(_anchor, index); i <= Math.Max(_anchor, index); i++) _selected.Add(i);
                _primary = index;
                InvalidateVisual(); SelectionChanged?.Invoke();
            }
            return;
        }
        if (!_pressPending || _pressPoint is not { } origin) return;
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - origin.X) < 4 && Math.Abs(point.Y - origin.Y) < 3) return;   // 拖动阈值
        if (!_textSelecting)
        {
            _textSelecting = true;
            _textAnchor = _pressLocation;
            e.Pointer.Capture(this);
        }
        if (BandAt(point) is { } location)
            _textHead = LocationAt(point, location.Row, location.Left);
        InvalidateVisual();
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging) { _dragging = false; e.Pointer.Capture(null); e.Handled = true; return; }
        if (!_pressPending) { if (_textSelecting) { e.Pointer.Capture(null); e.Handled = true; } return; }
        _pressPending = false;
        var location = _pressLocation;
        _pressPoint = null;
        e.Handled = true;
        if (_textSelecting) { _textSelecting = false; e.Pointer.Capture(null); return; }   // 拖选结束,保留文本选区
        ClearTextSelection();
        _selected.Clear(); _selected.Add(location.Row); _primary = _anchor = location.Row;
        InvalidateVisual(); SelectionChanged?.Invoke();
        EditRequested?.Invoke(_rows[location.Row], location.Left);
    }
    private (int Row, int Column, bool Left) LocationAt(Point point, int row, bool left)
    {
        var cell = left ? _rows[row].Left : _rows[row].Right;
        int length = cell?.Text.Length ?? 0;
        double textX = Band(row, left).X + NumberWidth;
        int column = Math.Clamp((int)((point.X - textX) / CharWidth), 0, length);
        return (row, column, left);
    }
    /// <summary>当前跨行文本选区(单侧);无选区返回空串。</summary>
    public string SelectedText()
    {
        if (_textAnchor is not { } a || _textHead is not { } b || a.Left != b.Left) return "";
        var (from, to) = a.Row <= b.Row ? (a, b) : (b, a);
        var builder = new System.Text.StringBuilder();
        for (int i = from.Row; i <= to.Row; i++)
        {
            var cell = from.Left ? _rows[i].Left : _rows[i].Right;
            if (cell is null) { builder.AppendLine(); continue; }
            int start = i == from.Row ? from.Column : 0;
            int end = i == to.Row ? Math.Min(to.Column, cell.Text.Length) : cell.Text.Length;
            if (end > start) builder.Append(cell.Text[start..end]);
            if (i < to.Row) builder.Append('\n');
        }
        return builder.ToString();
    }
    public void ClearTextSelection()
    {
        if (_textAnchor is null && _textHead is null) return;
        _textAnchor = _textHead = null;
        InvalidateVisual();
    }
    /// <summary>测试/程序化入口:设置跨行文本选区。</summary>
    public void SetTextSelection(int fromRow, int fromColumn, int toRow, int toColumn, bool left)
    {
        _textAnchor = (fromRow, fromColumn, left);
        _textHead = (toRow, toColumn, left);
        InvalidateVisual();
    }

    private Point? _pressPoint;
    private (int Row, int Column, bool Left) _pressLocation;
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
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if ((e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) != 0 && e.Key is >= Key.D1 and <= Key.D9)
        {
            int digit = e.Key - Key.D1 + 1;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) ToggleBookmark(Math.Max(0, _primary), digit);
            else GoToBookmark(digit);
            e.Handled = true;
            return;
        }
        if (e.Key != Key.C || (e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) == 0) return;
        string text = SelectedText();
        if (text.Length == 0) return;
        TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
        e.Handled = true;
    }
    public int IndexOfRow(AlignedRow row) => IndexOf(row);
    private int IndexOf(AlignedRow row)
    {
        for (int i = 0; i < _rows.Count; i++) if (ReferenceEquals(_rows[i], row)) return i;
        return -1;
    }
}
