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

    private IReadOnlyList<AlignedRow> _rows = [];
    private (int Row, int Column, bool Left)? _textAnchor, _textHead;
    private bool _textSelecting, _pressPending;
    private readonly HashSet<int> _selected = [];
    private int _primary = -1, _anchor = -1;
    private bool _dragging;
    private bool _vertical;
    private readonly Dictionary<int, int> _bookmarks = new();
    // ─── 查找:两侧的行文本里都找。行集合一换就按原查询重找 ───
    private readonly List<(int Row, bool Left, int Start, int Length)> _findMatches = [];
    private Dictionary<int, List<(bool Left, int Start, int Length)>> _findInRow = [];
    private int _findCurrent = -1;
    private string _findQuery = "";
    private bool _findIgnoreCase = true;
    private static readonly IBrush FindMatchBrush = new SolidColorBrush(Color.FromArgb(0x70, 0xF5, 0xA6, 0x23));
    private static readonly IBrush FindCurrentBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xC2, 0x6A, 0x00));
    private static readonly Pen FindCurrentPen = new(FindCurrentBrush, 1.5);

    public AlignedRowsView()
    {
        Focusable = true;
        // 订阅与可视树生命周期绑定:脱离时必须退订静态事件,否则窗口关闭后控件被 Palette 永久强引用(泄漏+重绘)。
        AttachedToVisualTree += (_, _) => { Palette.Changed -= OnPaletteChanged; Palette.Changed += OnPaletteChanged; };
        DetachedFromVisualTree += (_, _) => Palette.Changed -= OnPaletteChanged;
    }
    private void OnPaletteChanged() => InvalidateVisual();
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
    public IReadOnlyList<AlignedRow> Rows { get => _rows; set { _rows = value; _selected.Clear(); _primary = _anchor = -1; _textAnchor = _textHead = null; ResetPointerState(); SetFind(_findQuery, _findIgnoreCase); InvalidateMeasure(); InvalidateVisual(); } }
    /// <summary>一侧的文字变了而对齐不动:换成跟随后的行(见 RowFollow)。选中的行、书签、跨行选区按"还是同一行"跟过去;
    /// 与 Rows 的 set 不同 —— 那个是换了一份比较结果,选中自然作废。</summary>
    internal void FollowRows(FollowedRows followed)
    {
        var rows = followed.Rows; var map = followed.OldToNew;
        int? Move(int index) => index >= 0 && index < map.Count && map[index] >= 0 ? map[index] : null;
        var selected = _selected.Select(Move).Where(index => index is not null).Select(index => index!.Value).ToArray();
        _selected.Clear(); foreach (int index in selected) _selected.Add(index);
        _primary = Move(_primary) ?? -1; _anchor = Move(_anchor) ?? -1;
        if (_textAnchor is { } selectionStart && _textHead is { } selectionEnd)
        {
            // A selection across rows that lost one of its ends is no longer a selection.
            if (Move(selectionStart.Row) is not { } startRow || Move(selectionEnd.Row) is not { } endRow) _textAnchor = _textHead = null;
            else { _textAnchor = selectionStart with { Row = startRow }; _textHead = selectionEnd with { Row = endRow }; }
        }
        if (_bookmarks.Count > 0)
        {
            var marks = _bookmarks.ToArray(); _bookmarks.Clear();
            foreach (var mark in marks) if (Move(mark.Key) is { } moved) _bookmarks[moved] = mark.Value;
        }
        _rows = rows;
        SetFind(_findQuery, _findIgnoreCase);
        InvalidateMeasure(); InvalidateVisual();
    }
    public int RowCount => _rows.Count;
    public IReadOnlyList<AlignedRow> SelectedRows => _selected.OrderBy(i => i).Select(i => _rows[i]).ToList();
    public int PrimarySelected => _primary;
    /// <summary>行文本区左键(请求就地编辑):(行, 作用左/右侧)。</summary>
    /// <summary>语法着色(按侧):null=默认墨色。宿主按文件扩展名从 IFileTypeProvider 接入。</summary>
    public Func<string, IReadOnlyList<SyntaxToken>?>? SyntaxLeft, SyntaxRight;
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
        double bandStep = RowHeight * (_vertical ? 2 : 1);   // 上下布局中一行占两条 band(左上/右下)
        double top = index * bandStep, bottom = top + bandStep;
        if (top < scroller.Offset.Y) scroller.Offset = new(scroller.Offset.X, top);
        else if (bottom > scroller.Offset.Y + scroller.Viewport.Height)
            scroller.Offset = new(scroller.Offset.X, bottom - scroller.Viewport.Height);
    }
    public Rect CellRect(int index, bool left)
    {
        Rect band = Band(index, left);
        return new Rect(band.X + NumberWidth, band.Y, band.Width - NumberWidth, RowHeight);
    }
    private void DrawBookmark(DrawingContext context, int index, int digit)
    {
        Rect band = Band(index, true);
        var rect = new Rect(band.X + 1, band.Y + 3, 14, RowHeight - 6);
        context.FillRectangle(Palette.Bookmark, rect, 3);
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
    /// <summary>匹配的总数。</summary>
    public int FindMatchCount => _findMatches.Count;
    /// <summary>当前匹配的序号(从 0 起);没有匹配为 -1。</summary>
    public int FindIndex => _findCurrent;
    /// <summary>当前匹配:行、侧、在行文本里的起点与长度。</summary>
    public (int Row, bool Left, int Start, int Length)? FindMatch => _findCurrent >= 0 ? _findMatches[_findCurrent] : null;

    /// <summary>按 <paramref name="query"/> 重新找一遍,按行、左侧先于右侧、起点的顺序排列。
    /// 当前匹配还是原来那一处(如果它仍是匹配);否则取原行上的第一处,原行没有就取其后第一处,再没有就回到第一处 —— 眼睛不用跟着跳远。</summary>
    public void SetFind(string query, bool ignoreCase = true)
    {
        (int Row, bool Left, int Start, int Length)? was = FindMatch;
        _findQuery = query ?? "";
        _findIgnoreCase = ignoreCase;
        _findMatches.Clear();
        if (_findQuery.Length > 0)
        {
            var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            for (int i = 0; i < _rows.Count; i++)
                foreach (bool left in new[] { true, false })
                {
                    var text = (left ? _rows[i].Left : _rows[i].Right)?.Text;
                    if (string.IsNullOrEmpty(text)) continue;
                    for (int at = text.IndexOf(_findQuery, comparison); at >= 0; at = text.IndexOf(_findQuery, at + _findQuery.Length, comparison))
                        _findMatches.Add((i, left, at, _findQuery.Length));
                }
        }
        _findInRow = _findMatches.GroupBy(match => match.Row).ToDictionary(group => group.Key, group => group.Select(match => (match.Left, match.Start, match.Length)).ToList());
        _findCurrent = was is { } keep ? _findMatches.IndexOf(keep) : -1;
        if (_findCurrent < 0 && _findMatches.Count > 0)
            _findCurrent = Math.Max(0, _findMatches.FindIndex(match => match.Row >= (was?.Row ?? 0)));
        InvalidateVisual();
    }

    /// <summary>走到下一处 / 上一处(首尾相接),选中它所在的行并滚动到可见。没有匹配时返回 false。</summary>
    public bool FindNext(bool backwards = false)
    {
        if (_findMatches.Count == 0) return false;
        _findCurrent = _findCurrent < 0 ? (backwards ? _findMatches.Count - 1 : 0)
            : (_findCurrent + (backwards ? -1 : 1) + _findMatches.Count) % _findMatches.Count;
        var match = _findMatches[_findCurrent];
        _selected.Clear(); _selected.Add(match.Row); _primary = _anchor = match.Row;
        EnsureVisible(match.Row);
        InvalidateVisual();
        SelectionChanged?.Invoke();
        return true;
    }

    /// <summary>查找匹配:文字下面铺一层底色,当前匹配再描一圈边。只画可见前缀里的部分。</summary>
    private void DrawFindMatches(DrawingContext context, int index, bool left, int drawnLength, double textX, double charWidth, Rect band)
    {
        if (!_findInRow.TryGetValue(index, out var matches)) return;
        foreach (var match in matches)
        {
            if (match.Left != left) continue;
            int start = Math.Clamp(match.Start, 0, drawnLength), length = Math.Clamp(match.Length, 0, drawnLength - start);
            if (length <= 0) continue;
            var rect = new Rect(textX + start * charWidth, band.Y + 2, length * charWidth, RowHeight - 5);
            context.FillRectangle(FindMatchBrush, rect, 2);
            if (_findCurrent >= 0 && _findMatches[_findCurrent] == (index, left, match.Start, match.Length))
                context.DrawRectangle(FindCurrentPen, rect.Deflate(0.75), 2);
        }
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
                context.FillRectangle(Palette.TextSelection, new Rect(Math.Min(x0, x1), band.Y + 1, Math.Max(2, Math.Abs(x1 - x0)), RowHeight - 2), 2);
            }
        }
    }
    private void DrawSide(DrawingContext context, AlignedRow row, int index, bool left)
    {
        TextCell? cell = left ? row.Left : row.Right;
        Rect band = Band(index, left);
        IBrush? tint = cell is null ? Palette.Ghost
            : row.IsDifferent ? (left ? (row.Right is null ? Palette.Deleted : Palette.Modified) : (row.Left is null ? Palette.Inserted : Palette.Modified))
            : null;
        if (_selected.Contains(index))
        {
            context.FillRectangle(Palette.Selected, band);
            if (tint is not null)
                using (context.PushOpacity(0.45))
                    context.FillRectangle(tint, band);   // 选中时差异色以 45% 叠加
        }
        else if (tint is not null) context.FillRectangle(tint, band);
        if (cell is null) return;
        var number = new FormattedText(cell.LineNumber.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, 11, Palette.Number);
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
                context.FillRectangle(Palette.Word, new Rect(textX + start * charWidth, band.Y + 2, length * charWidth, RowHeight - 5), 2);
        }
        DrawFindMatches(context, index, left, drawn.Length, textX, charWidth, band);
        // 语法着色:令牌分彩段绘制(等宽字体按字符宽推进,避免逐字符排版开销)
        var syntax = (left ? SyntaxLeft : SyntaxRight)?.Invoke(cell.Text);
        double y = band.Y + (RowHeight - TextSize * 1.35) / 2;   // 与居中编辑器的文本基线对齐
        if (syntax is { Count: > 0 })
        {
            int cursor = 0;
            foreach (var token in syntax)
            {
                int start = Math.Clamp(token.Start, 0, drawn.Length), length = Math.Clamp(token.Length, 0, drawn.Length - start);
                if (start > cursor)
                    context.DrawText(new FormattedText(drawn[cursor..start], CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, TextSize, Palette.Ink), new Point(textX + cursor * charWidth, y));
                if (length > 0)
                    context.DrawText(new FormattedText(drawn[start..(start + length)], CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, TextSize, TokenBrush(token.Kind)), new Point(textX + start * charWidth, y));
                cursor = Math.Max(cursor, start + length);
            }
            if (cursor < drawn.Length)
                context.DrawText(new FormattedText(drawn[cursor..], CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, TextSize, Palette.Ink), new Point(textX + cursor * charWidth, y));
        }
        else
        {
            var text = new FormattedText(drawn, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, TextSize, Palette.Ink);
            context.DrawText(text, new Point(textX, y));
        }
        if (cell.Text.Length > drawn.Length)
        {
            var more = new FormattedText("…", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, TextSize, Palette.Number);
            context.DrawText(more, new Point(textX + drawn.Length * charWidth, band.Y + 3));
        }
        if (cell.Ending is "CRLF" or "CR")
        {
            var marker = new FormattedText(cell.Ending == "CRLF" ? "⏎CRLF" : "⏎CR", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, 10, Palette.Number);
            context.DrawText(marker, new Point(band.Right - MarkerWidth + 4, band.Y + 5));
        }
    }
    private static IBrush TokenBrush(SyntaxTokenKind kind) => kind switch
    {
        SyntaxTokenKind.Keyword => Palette.Keyword,
        SyntaxTokenKind.String => Palette.String,
        SyntaxTokenKind.Comment => Palette.Comment,
        SyntaxTokenKind.Number => Palette.NumberBrush,
        SyntaxTokenKind.Type => Palette.Type,
        _ => Palette.Operator,
    };
    private static double _charWidth;
    private static double CharWidth => _charWidth != 0 ? _charWidth : _charWidth = new FormattedText("M", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, TextSize, Palette.Ink).Width;
    /// <summary>一行文本里一个字符的宽度(等宽字体);查找匹配的位置按它算。</summary>
    public double CharacterWidth => CharWidth;

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
            // 右键即清除陈旧文本选区:否则旧的拖选范围会劫持"复制此行到对侧"(CopyToSide 优先读选区,审查发现)。
            ClearTextSelection();
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
            int slot = (int)(e.GetPosition(this).Y / RowHeight);
            int index = Math.Clamp(_vertical ? slot / 2 : slot, 0, _rows.Count - 1);
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
        if (location.Row >= _rows.Count) return;   // 按住期间行集被替换:越界即放弃本次点击,不误编辑错误行
        ClearTextSelection();
        _selected.Clear(); _selected.Add(location.Row); _primary = _anchor = location.Row;
        InvalidateVisual(); SelectionChanged?.Invoke();
        EditRequested?.Invoke(_rows[location.Row], location.Left);
    }
    /// <summary>行集替换(重对齐/新文件)时丢弃进行中的指针交互:陈旧的按下/拖选状态指向旧行号,会误编辑甚至越界。</summary>
    private void ResetPointerState()
    {
        _pressPending = false;
        _pressPoint = null;
        _textSelecting = false;
        _dragging = false;
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
