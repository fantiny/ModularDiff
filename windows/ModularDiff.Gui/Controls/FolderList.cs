using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ModularDiff.Gui.Controls;

/// <summary>
/// The list of the folder view: rows of one fixed height, only the visible ones of which exist.
/// A row's visuals are built once and live as long as the list — scrolling, or a new row set on
/// every keystroke in the filter box, just points a row at another node and its bindings refresh
/// the text in place. That is what keeps filter-as-you-type cheap: rebuilding row containers,
/// which items-control lists do whenever their items are replaced, cost more than everything
/// else in a filtering pass combined.
/// </summary>
/// <remarks>
/// Selection is the extended selection of a grid (click, Ctrl+click, Shift+click, the arrow,
/// page and home/end keys with an optional Shift); the column titles come in through
/// <see cref="Header"/> and stay aligned with the rows.
/// </remarks>
[TemplatePart(Name = "PART_Scroll", Type = typeof(ScrollViewer))]
[TemplatePart(Name = "PART_Rows", Type = typeof(FolderRowHost))]
[TemplatePart(Name = "PART_HeaderShift", Type = typeof(TranslateTransform))]
public sealed class FolderList : Control
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(object), typeof(FolderList),
        new PropertyMetadata(null, (list, args) => ((FolderList)list).SwapHeader(args.OldValue, args.NewValue)));

    public static readonly DependencyProperty RowTemplateProperty = DependencyProperty.Register(
        nameof(RowTemplate), typeof(DataTemplate), typeof(FolderList),
        new PropertyMetadata(null, (list, args) => ((FolderList)list)._host?.RowTemplate = (DataTemplate?)args.NewValue));

    /// <summary>Width the header keeps free for the vertical scrollbar, so that its columns stay
    /// above the rows' columns.</summary>
    public static readonly DependencyProperty ScrollGutterProperty = DependencyProperty.Register(
        nameof(ScrollGutter), typeof(double), typeof(FolderList), new PropertyMetadata(0.0));

    private FolderRowHost? _host;
    private ScrollViewer? _scroll;
    private TranslateTransform? _headerShift;
    private IReadOnlyList<FolderNode> _rows = [];
    private readonly List<FolderNode> _selection = [];
    private int _anchor = -1;   // fixed end of a Shift+click / Shift+arrow range

    public FolderList()
    {
        Focusable = true;
        SelectedItems = new SelectionList(this);
    }

    /// <summary>The column titles, as a visual. It is a logical child, so it takes part in
    /// everything that walks the window (localization).</summary>
    public object? Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }

    /// <summary>How a row shows its node.</summary>
    public DataTemplate? RowTemplate { get => (DataTemplate?)GetValue(RowTemplateProperty); set => SetValue(RowTemplateProperty, value); }

    /// <summary>Width reserved at the right of the header for the vertical scrollbar.</summary>
    public double ScrollGutter => (double)GetValue(ScrollGutterProperty);

    /// <summary>The rows, in the order they are shown.</summary>
    public IReadOnlyList<FolderNode> Rows => _rows;

    /// <summary>The primary selected node: the one the keyboard acts on.</summary>
    public FolderNode? SelectedItem
    {
        get => _selection.FirstOrDefault();
        set
        {
            int index = value is null ? -1 : IndexOf(value);
            if (index < 0) { if (_selection.Count > 0) SetSelection([], -1); }   // as in a grid: a node that is not listed selects nothing
            else SetSelection([value!], index);
        }
    }

    /// <summary>Every selected node. Editing it keeps the rows and the status line in step.</summary>
    public ICollection<FolderNode> SelectedItems { get; }

    public event EventHandler? SelectionChanged;

    /// <summary>Shows <paramref name="rows"/> in order. The pooled rows take over the nodes now at
    /// their positions; a selection of nodes that are gone goes away with them.</summary>
    public void SetRows(IReadOnlyList<FolderNode> rows)
    {
        _rows = rows;
        _host?.InvalidateMeasure();
        if (_selection.RemoveAll(node => !rows.Contains(node)) > 0) SelectionEdited();
    }

    public void ScrollIntoView(FolderNode node)
    {
        int index = IndexOf(node);
        if (index >= 0) _host?.BringRowIntoView(index);
    }

    public void ScrollToTop() => _host?.ScrollToTop();

    /// <summary>Re-points every row at its node: for texts the node computes on read (a language switch).</summary>
    public void Refresh() => _host?.RefreshRows();

    /// <summary>The node of the row under the pointer, or null beside the rows.</summary>
    public FolderNode? RowAt(MouseEventArgs e)
    {
        if (_host is null) return null;
        double y = e.GetPosition(_host).Y;
        int index = _host.FirstRow + (int)(y / _host.RowHeight);
        return y >= 0 && index < _rows.Count ? _rows[index] : null;
    }

    private int IndexOf(FolderNode node)
    {
        for (int i = 0; i < _rows.Count; i++) if (ReferenceEquals(_rows[i], node)) return i;
        return -1;
    }

    internal bool IsSelected(FolderNode node) => _selection.Contains(node);
    internal bool IsCurrent(FolderNode node) => IsKeyboardFocusWithin && ReferenceEquals(SelectedItem, node);

    public override void OnApplyTemplate()
    {
        if (_scroll is not null) { _scroll.ScrollChanged -= OnScrolled; _scroll.SizeChanged -= OnResized; }
        base.OnApplyTemplate();
        _scroll = GetTemplateChild("PART_Scroll") as ScrollViewer;
        _headerShift = GetTemplateChild("PART_HeaderShift") as TranslateTransform;
        _host = GetTemplateChild("PART_Rows") as FolderRowHost;
        if (_host is not null) { _host.List = this; _host.RowTemplate = RowTemplate; }
        if (_scroll is not null) { _scroll.ScrollChanged += OnScrolled; _scroll.SizeChanged += OnResized; }
        AlignHeader();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _host?.InvalidateMeasure();   // the current row's focus cue follows the keyboard, not the mouse
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _host?.InvalidateMeasure();
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (e.Handled) return;
        Focus();
        FolderNode? node = RowAt(e);
        int index = node is null ? -1 : IndexOf(node);
        if (e.ClickCount >= 2)
        {
            // The second click of a double-click opens the row (the window's handler runs after this
            // one): the row must be selected by then, and a modifier must not toggle it away.
            if (node is not null && !IsSelected(node)) SelectedItem = node;
            return;
        }
        if (node is null) { if (_selection.Count > 0) SetSelection([], -1); return; }
        var modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            var toggled = _selection.ToList();   // toggle this row, keep the others
            if (!toggled.Remove(node)) toggled.Add(node);
            SetSelection(toggled, index);
        }
        else if ((modifiers & ModifierKeys.Shift) != 0 && _anchor >= 0)
            SetSelection(Range(Math.Min(_anchor, index), Math.Max(_anchor, index)), _anchor);
        else
            SetSelection([node], index);
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (e.Handled) return;
        if (RowAt(e) is { } node && !IsSelected(node)) SelectedItem = node;   // the context menu acts on the clicked row
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _rows.Count == 0) return;
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.A && (modifiers & ModifierKeys.Control) != 0)   // as in a grid: select every row
        {
            e.Handled = true;
            SetSelection(_rows.ToList(), _anchor);
            return;
        }
        int current = _anchor >= 0 ? Math.Min(_anchor, _rows.Count - 1) : -1;   // nothing selected yet: the keys enter at the top
        int page = _host is null ? 1 : Math.Max(1, _host.RowCount - 1);
        int target = e.Key switch
        {
            Key.Down => Math.Min(_rows.Count - 1, current + 1),
            Key.Up => Math.Max(0, current - 1),
            Key.PageDown => current < 0 ? 0 : Math.Min(_rows.Count - 1, current + page),
            Key.PageUp => Math.Max(0, current - page),
            Key.Home => 0,
            Key.End => _rows.Count - 1,
            _ => -1,
        };
        if (target < 0) return;
        e.Handled = true;
        bool extend = (modifiers & ModifierKeys.Shift) != 0;
        if (extend && _anchor >= 0) SetSelection(Range(Math.Min(_anchor, target), Math.Max(_anchor, target)), _anchor);
        else SetSelection([_rows[target]], target);
        _host?.BringRowIntoView(target);
    }

    /// <summary>The header is a logical child, as the header of a HeaderedContentControl is: what
    /// walks the window (localization, the chrome smoke) must see its captions.</summary>
    protected override IEnumerator LogicalChildren
    {
        get
        {
            if (Header is { } header) yield return header;
        }
    }

    private void SwapHeader(object? oldHeader, object? newHeader)
    {
        if (oldHeader is Visual or FrameworkContentElement) RemoveLogicalChild(oldHeader);
        if (newHeader is Visual or FrameworkContentElement) AddLogicalChild(newHeader);
    }

    private void OnScrolled(object sender, ScrollChangedEventArgs e) => AlignHeader();
    private void OnResized(object sender, SizeChangedEventArgs e) => AlignHeader();

    /// <summary>Keeps the header above the rows: it moves sideways with them and leaves the
    /// scrollbar its width.</summary>
    private void AlignHeader()
    {
        if (_scroll is not null)
        {
            double gutter = _scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible ? SystemParameters.VerticalScrollBarWidth : 0;
            if (gutter != ScrollGutter) SetValue(ScrollGutterProperty, gutter);
        }
        if (_headerShift is not null) _headerShift.X = -(_host?.HorizontalOffset ?? 0);
    }

    private List<FolderNode> Range(int from, int to) => _rows.Skip(from).Take(to - from + 1).ToList();

    private void SetSelection(List<FolderNode> nodes, int anchor)
    {
        _selection.Clear();
        _selection.AddRange(nodes);
        _anchor = anchor;
        _host?.InvalidateMeasure();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SelectionEdited()
    {
        _anchor = SelectedItem is { } primary ? IndexOf(primary) : -1;
        SetSelection(_selection.ToList(), _anchor);
    }

    /// <summary>The <see cref="SelectedItems"/> of a grid: an editable collection whose edits go
    /// through the list, so rows and listeners follow.</summary>
    private sealed class SelectionList(FolderList owner) : ICollection<FolderNode>
    {
        public int Count => owner._selection.Count;
        public bool IsReadOnly => false;
        public void Add(FolderNode item) { if (!owner._selection.Contains(item)) { owner._selection.Add(item); owner.SelectionEdited(); } }
        public void Clear() { if (owner._selection.Count > 0) { owner._selection.Clear(); owner.SelectionEdited(); } }
        public bool Remove(FolderNode item) { if (!owner._selection.Remove(item)) return false; owner.SelectionEdited(); return true; }
        public bool Contains(FolderNode item) => owner._selection.Contains(item);
        public void CopyTo(FolderNode[] array, int arrayIndex) => owner._selection.CopyTo(array, arrayIndex);
        public IEnumerator<FolderNode> GetEnumerator() => owner._selection.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
