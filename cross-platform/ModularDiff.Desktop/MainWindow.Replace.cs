using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using ModularDiff.Contracts;
using ModularDiff.Core;

namespace ModularDiff.Desktop;

/// <summary>替换改哪一侧:左侧、右侧,或者两侧都改(各是一次编辑;⌘Z 一步还原两侧)。</summary>
public enum ReplaceScope { Left, Right, Both }

public sealed partial class MainWindow
{
    // ─── 替换:查找条下面的一行,⇄ 或 ⌥⌘F 打开。替换一处换的是描边的那一处(所见即所换),然后走到它后面的下一处;
    //     全部替换作用于所选的一侧或两侧。WPF 版作用于"聚焦的那一侧",这里没有聚焦侧,所以摆一个选的。 ───
    private readonly Grid _replaceBar = new() { IsVisible = false, ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), Margin = new Thickness(26, 2, 0, 2) };
    private readonly TextBox _replaceBox = new() { MinWidth = 160, MaxWidth = 380, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _replaceScopeBox = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _replaceOneButton = new() { MinWidth = 84, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _replaceAllButton = new() { MinWidth = 84, Margin = new Thickness(8, 0, 0, 0) };
    private StringComparison FindComparison => _findCase.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private void BuildReplaceBar()
    {
        Tip(_replaceBox, "md_replace_text_tip");
        _replaceBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; ReplaceOne(); }
            else if (e.Key == Avalonia.Input.Key.Escape)
            {
                e.Handled = true;
                ReplaceMode = false; _findBox.Focus(); _findBox.SelectAll();   // leaves replace, keeps the search
            }
        };
        Tip(_replaceScopeBox, "md_replace_scope_tip");
        Tip(_replaceOneButton, "md_replace_one_tip"); _replaceOneButton.Click += (_, _) => ReplaceOne();
        Tip(_replaceAllButton, "md_replace_all_tip"); _replaceAllButton.Click += (_, _) => ReplaceAll();
        _findBar.RowDefinitions = new RowDefinitions("Auto,Auto");
        Grid.SetRow(_replaceBar, 1); Grid.SetColumnSpan(_replaceBar, 8); _findBar.Children.Add(_replaceBar);
        void Place(Control control, int column) { Grid.SetColumn(control, column); _replaceBar.Children.Add(control); }
        Place(_replaceBox, 0); Place(_replaceScopeBox, 1); Place(_replaceOneButton, 2); Place(_replaceAllButton, 3);
        LocalizeReplace();
    }

    private void LocalizeReplace()
    {
        _replaceBox.Watermark = Lang.Get("a11y_replace");
        _replaceOneButton.Content = Lang.Get("replace_one_button");
        _replaceAllButton.Content = Lang.Get("replace_all_button");
        int chosen = _replaceScopeBox.SelectedIndex;
        _replaceScopeBox.ItemsSource = new[] { Lang.Get("md_replace_scope_left"), Lang.Get("md_replace_scope_right"), Lang.Get("md_replace_scope_both") };
        _replaceScopeBox.SelectedIndex = Math.Max(0, chosen);
    }

    /// <summary>替换的一行露着(查找条和它都可见)。设为 true 会把查找条一并打开。</summary>
    public bool ReplaceMode
    {
        get => _replaceBar.IsVisible && _findBar.IsVisible;
        set { if (!value) _replaceBar.IsVisible = false; else { ShowFind(); _replaceBar.IsVisible = true; } }
    }
    public string ReplaceText { get => _replaceBox.Text ?? ""; set => _replaceBox.Text = value; }
    public ReplaceScope ReplaceScope { get => (ReplaceScope)Math.Clamp(_replaceScopeBox.SelectedIndex, 0, 2); set => _replaceScopeBox.SelectedIndex = (int)value; }
    public TextBox ReplaceBox => _replaceBox;
    public ComboBox ReplaceScopeBox => _replaceScopeBox;

    /// <summary>打开查找条并露出替换的一行,焦点进替换框。作用的一侧跟着当前匹配走(所见的那一侧);没有当前匹配就保持原样。</summary>
    public void ShowReplace()
    {
        ShowFind();
        if (_rows.FindMatch is { } match) _replaceScopeBox.SelectedIndex = (int)(match.Left ? ReplaceScope.Left : ReplaceScope.Right);
        _replaceBar.IsVisible = true;
        _replaceBox.Focus(); _replaceBox.SelectAll();
    }

    /// <summary>把当前匹配(描边的那一处)换成替换文字,然后走到它后面的下一处。返回是否换了。</summary>
    public bool ReplaceOne()
    {
        string find = _findBox.Text ?? "", replacement = _replaceBox.Text ?? "";
        if (find.Length == 0) { _status.Text = Lang.Get("status_enter_find"); return false; }
        if (_rows.FindMatch is not { } current) { _status.Text = Lang.Get("status_not_found_query", find); return false; }
        var (row, left, start, length) = current;
        var buffer = left ? _session.Left : _session.Right;
        if (buffer is null) { _status.Text = Lang.Get("status_readonly_file"); return false; }
        var cell = row < _rows.RowCount ? (left ? _rows.Rows[row].Left : _rows.Rows[row].Right) : null;
        string? text = cell is null ? null : TextReplace.ReplaceOne(buffer.Text, cell, start, length, find, replacement, FindComparison);
        if (text is null)
        {
            // 行集合和文件对不上(理论上不会:行跟着文字走)。一个字不写,把行集合按文件重排,请用户再来一次。
            _session.Recompare(); RefreshRows();
            _status.Text = Lang.Get("md_replace_mismatch");
            return false;
        }
        EditSide(left, text);
        _debounce.Stop(); RefreshDirty();
        if ((bool)_autoRealign.IsChecked!) Realign();
        // 往后走:替换文字里可能正好含有查找文字,所以从写下的这一段之后找起;后面没有了就回到第一处
        _rows.ShowMatch(TextReplace.AtOrAfter(_rows.FindMatches, row, left, start + replacement.Length));
        UpdateFindCount();
        _status.Text = Lang.Get("status_replaced_one", find, replacement);
        return true;
    }

    /// <summary>把选定的那一侧(或两侧)的每一处都换掉;每侧是一次编辑,⌘Z 一步还原。返回换掉的处数。
    /// 匹配与计数不用另外刷新:写入走 EditSide,行跟着文字走时已经重新找过了。</summary>
    public int ReplaceAll()
    {
        string find = _findBox.Text ?? "", replacement = _replaceBox.Text ?? "";
        if (find.Length == 0) { _status.Text = Lang.Get("status_enter_find"); return 0; }
        var scope = ReplaceScope; var comparison = FindComparison;
        var planned = new List<(bool Left, string Text, int Count)>();
        // 先都算好再写:有一侧打不开就整件不做,不能改了一侧留一侧
        foreach (bool left in scope == ReplaceScope.Both ? new[] { true, false } : new[] { scope == ReplaceScope.Left })
        {
            var buffer = left ? _session.Left : _session.Right;
            if (buffer is null) { _status.Text = Lang.Get("status_readonly_file"); return 0; }
            planned.Add((left, TextReplace.ReplaceAll(buffer.Text, find, replacement, comparison, out int count), count));
        }
        int total = planned.Sum(plan => plan.Count);
        if (total == 0) { _status.Text = Lang.Get("status_not_found_query", find); return 0; }
        foreach (var plan in planned)
            if (plan.Count > 0) EditSide(plan.Left, plan.Text);
        _debounce.Stop(); RefreshDirty();
        if ((bool)_autoRealign.IsChecked!) Realign();
        _status.Text = Lang.Get("status_replaced_n", total, find, replacement);
        return total;
    }
}
