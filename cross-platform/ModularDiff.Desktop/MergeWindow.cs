using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ModularDiff.Contracts;
using ModularDiff.Core;

namespace ModularDiff.Desktop;

/// <summary>三方合并窗口(与 WPF MergeWindow 同契约):BASE/LOCAL/REMOTE → 带冲突标记的结果编辑器;
/// 冲突导航与"采用左/右侧"逐个决策;git mergetool 模式下关闭时写入 MERGED 并以 GitMergeCompleted(是否无冲突)回报退出码。</summary>
public sealed class MergeWindow : Window
{
    // 文字全部取自语言包:这个窗口此前从不用 Lang,任何界面语言下都是中文。
    private readonly TextBox _base = new() { Watermark = Lang.Get("md_merge_base") }, _left = new() { Watermark = Lang.Get("md_merge_local") }, _right = new() { Watermark = Lang.Get("md_merge_remote") };
    private readonly TextBox _result = new() { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Code,Menlo,DejaVu Sans Mono,monospace"), FontSize = 13, IsReadOnly = false };
    private readonly TextBlock _status = new() { Text = Lang.Get("md_merge_start_hint"), TextWrapping = TextWrapping.Wrap };
    private List<(int Start, int Middle, int End)> _conflicts = [];
    private int _currentConflict = -1;
    private bool _everMerged;
    private readonly string? _gitOutputPath;
    /// <summary>git mergetool 的 MERGED 输出路径;只能经构造函数传入 —— 此前为 init-only 属性,
    /// 对象初始化器在构造函数之后执行,构造函数里据此订阅 Closing 是永远不生效的死接线(审查 K1)。</summary>
    public string? GitOutputPath => _gitOutputPath;
    public int ConflictCount => _conflicts.Count;
    /// <summary>把当前结果写入 git 的 MERGED 路径并回报完成状态(Closing 与测试都走这里;行尾按系统惯例,原子写)。
    /// 从未执行合并就关闭时 fail-closed:不写 MERGED(git 保留自己的冲突版本),退出码非 0 表示未解决。</summary>
    public void SaveToOutput()
    {
        if (_gitOutputPath is null) return;
        if (!_everMerged) { GitMergeCompleted?.Invoke(false); return; }
        try
        {
            AtomicFile.WriteAllText(_gitOutputPath, (_result.Text ?? "").Replace("\r\n", "\n").Replace("\n", Environment.NewLine));
            GitMergeCompleted?.Invoke(_conflicts.Count == 0);
        }
        catch { GitMergeCompleted?.Invoke(false); }
    }
    /// <summary>git mergetool 流程完成(参数=是否无未决冲突);宿主据此设置进程退出码 0/1。</summary>
    public event Action<bool>? GitMergeCompleted;
    /// <summary>状态栏与三个路径框(测试核对它们的文字跟随界面语言)。</summary>
    public string StatusText => _status.Text ?? "";
    public IReadOnlyList<TextBox> PathBoxes => [_base, _left, _right];
    /// <summary>测试/程序化入口:结果编辑器(模拟用户手编,验证 TextChanged → 重析接线)。</summary>
    public TextBox ResultEditor => _result;

    private readonly PluginCatalog _plugins;
    public MergeWindow(string? basePath = null, string? leftPath = null, string? rightPath = null, PluginCatalog? plugins = null, string? gitOutputPath = null)
    {
        _plugins = plugins ?? App.Plugins ?? throw new InvalidOperationException(Lang.Get("md_merge_no_plugins"));
        _gitOutputPath = gitOutputPath;
        Title = Lang.Get("merge_window_title"); Width = 1150; Height = 780; MinWidth = 780; MinHeight = 520;
        _base.Text = basePath; _left.Text = leftPath; _right.Text = rightPath;
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        Button Button(string caption, string tip, Action action)
        {
            var button = new Button { Content = caption, MinWidth = 34, Padding = new Thickness(8, 4) };
            ToolTip.SetTip(button, tip); button.Click += (_, _) => action(); return button;
        }
        toolbar.Children.Add(Button("▶", Lang.Get("merge_run_tip"), async () => await MergeAsync()));
        toolbar.Children.Add(Button("↑", Lang.Get("merge_prev"), () => Navigate(-1)));
        toolbar.Children.Add(Button("↓", Lang.Get("merge_next"), () => Navigate(1)));
        toolbar.Children.Add(Button("⇐", Lang.Get("merge_take_left_tip"), () => Take(false)));
        toolbar.Children.Add(Button("⇒", Lang.Get("merge_take_right_tip"), () => Take(true)));
        var paths = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        paths.Children.Add(_base);
        var mid = _left; Grid.SetColumn(mid, 1); paths.Children.Add(mid);
        var right = _right; Grid.SetColumn(right, 2); paths.Children.Add(right);
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(10), RowSpacing = 8 };
        root.Children.Add(toolbar); Grid.SetRow(paths, 1); root.Children.Add(paths);
        Grid.SetRow(_result, 2); root.Children.Add(_result);
        Grid.SetRow(_status, 3); root.Children.Add(_status);
        Content = root;
        if (_gitOutputPath is not null)
            Closing += (_, _) => SaveToOutput();
        // 结果区自由手编:任何改动立即重析冲突标记,导航/决策永远作用于当前文本
        // (此前 _conflicts 保存旧行号,删行后 ↑↓/⇐⇒ 越界崩溃 —— 审查 K5)。
        _result.TextChanged += (_, _) => Repaint();
        Opened += async (_, _) => { if (basePath is not null && leftPath is not null && rightPath is not null) await MergeAsync(); };
    }
    /// <summary>执行三方合并(公开为测试/程序化入口;算法取目录中第一个算法插件,不再硬编码)。</summary>
    public async Task MergeAsync()
    {
        try
        {
            if (!File.Exists(_base.Text) || !File.Exists(_left.Text) || !File.Exists(_right.Text))
            { _status.Text = Lang.Get("merge_need_three_files"); return; }
            _status.Text = Lang.Get("merge_working");
            string baseText = await File.ReadAllTextAsync(_base.Text!), leftText = await File.ReadAllTextAsync(_left.Text!), rightText = await File.ReadAllTextAsync(_right.Text!);
            static string[] Split(string text) => text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
            string algorithm = _plugins.GetAll<IDiffAlgorithm>().FirstOrDefault()?.Id ?? "myers";
            var merged = await Task.Run(() => ThreeWayMerger.Merge(Split(baseText), Split(leftText), Split(rightText), algorithm, _plugins));
            string newLine = leftText.Contains("\r\n") ? "\r\n" : "\n";
            string composed = merged.Compose(Path.GetFileName(_left.Text!), Path.GetFileName(_right.Text!), newLine);
            _result.Text = composed.Replace("\r\n", "\n");
            Repaint();
            _everMerged = true;
        }
        catch (Exception ex) { _status.Text = Lang.Get("merge_failed", Lang.Text(ex.Message)); }
    }
    /// <summary>重新解析冲突标记并刷新导航状态(结果可自由手编,决策始终作用于当前标记结构)。</summary>
    public void Repaint()
    {
        _conflicts = [];
        var lines = (_result.Text ?? "").Split('\n');
        int start = -1, middle = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            string text = lines[i].TrimStart();
            if (text.StartsWith("<<<<<<<")) { start = i; middle = -1; }
            else if (text.StartsWith("=======") && start >= 0) middle = i;
            else if (text.StartsWith(">>>>>>>") && start >= 0 && middle >= 0) { _conflicts.Add((start, middle, i)); start = middle = -1; }
        }
        _currentConflict = _conflicts.Count > 0 ? 0 : -1;
        _status.Text = _conflicts.Count == 0 ? Lang.Get("merge_clean_status") : Lang.Get("md_merge_conflicts_hint", _conflicts.Count);
    }
    private void Navigate(int direction)
    {
        if (_conflicts.Count == 0) { _status.Text = Lang.Get("merge_no_conflicts"); return; }
        _currentConflict = Math.Clamp(_currentConflict + direction, 0, _conflicts.Count - 1);
        var (start, _, end) = _conflicts[_currentConflict];
        _status.Text = Lang.Get("md_merge_conflict_at", _currentConflict + 1, _conflicts.Count, start + 1, end + 1);
        var lines = _result.Text!.Split('\n');
        if (start >= lines.Length) { Repaint(); return; }   // 防御:行号失效(理论上 TextChanged 已重析)
        _result.SelectionStart = lines[..start].Aggregate(0, (sum, line) => sum + line.Length + 1);
        _result.SelectionEnd = _result.SelectionStart + lines[start].Length;
        _result.Focus();
    }
    /// <summary>解决当前冲突:true=采用右侧,false=采用左侧;重写结果文本后重新解析。</summary>
    public void Take(bool useRight)
    {
        if (_currentConflict < 0 || _currentConflict >= _conflicts.Count) { _status.Text = Lang.Get("md_merge_pick_conflict"); return; }
        var (start, middle, end) = _conflicts[_currentConflict];
        var lines = (_result.Text ?? "").Split('\n').ToList();
        if (start >= lines.Count || end >= lines.Count || middle >= lines.Count) { Repaint(); _status.Text = Lang.Get("md_merge_markers_changed"); return; }
        var chosen = useRight ? lines.GetRange(middle + 1, end - middle - 1) : lines.GetRange(start + 1, middle - start - 1);
        lines.RemoveRange(start, end - start + 1);
        lines.InsertRange(start, chosen);
        _result.Text = string.Join("\n", lines);
        Repaint();
        _status.Text = Lang.Get("merge_took_side", Lang.Get(useRight ? "side_right" : "side_left"), _conflicts.Count);
    }
}
