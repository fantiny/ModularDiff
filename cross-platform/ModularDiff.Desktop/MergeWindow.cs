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
    private readonly TextBox _base = new() { Watermark = "公共祖先 (BASE)" }, _left = new() { Watermark = "左侧（我的）LOCAL" }, _right = new() { Watermark = "右侧（对方的）REMOTE" };
    private readonly TextBox _result = new() { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Code,Menlo,DejaVu Sans Mono,monospace"), FontSize = 13, IsReadOnly = false };
    private readonly TextBlock _status = new() { Text = "填三个文件路径(或由 git mergetool 传入),点 ▶ 合并。", TextWrapping = TextWrapping.Wrap };
    private List<(int Start, int Middle, int End)> _conflicts = [];
    private int _currentConflict = -1;
    public string? GitOutputPath { get; init; }
    public int ConflictCount => _conflicts.Count;
    /// <summary>把当前结果写入 git 的 MERGED 路径并回报完成状态(Closing 与测试都走这里;行尾按系统惯例)。</summary>
    public void SaveToOutput()
    {
        if (GitOutputPath is null) return;
        try
        {
            File.WriteAllText(GitOutputPath, (_result.Text ?? "").Replace("\r\n", "\n").Replace("\n", Environment.NewLine));
            GitMergeCompleted?.Invoke(_conflicts.Count == 0);
        }
        catch { GitMergeCompleted?.Invoke(false); }
    }
    /// <summary>git mergetool 流程完成(参数=是否无未决冲突);宿主据此设置进程退出码 0/1。</summary>
    public event Action<bool>? GitMergeCompleted;

    private readonly PluginCatalog _plugins;
    public MergeWindow(string? basePath = null, string? leftPath = null, string? rightPath = null, PluginCatalog? plugins = null)
    {
        _plugins = plugins ?? App.Plugins ?? throw new InvalidOperationException("插件目录不可用。");
        Title = "ModularDiff · 三方合并"; Width = 1150; Height = 780; MinWidth = 780; MinHeight = 520;
        _base.Text = basePath; _left.Text = leftPath; _right.Text = rightPath;
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        Button Button(string caption, string tip, Action action)
        {
            var button = new Button { Content = caption, MinWidth = 34, Padding = new Thickness(8, 4) };
            ToolTip.SetTip(button, tip); button.Click += (_, _) => action(); return button;
        }
        toolbar.Children.Add(Button("▶", "执行三方合并", async () => await MergeAsync()));
        toolbar.Children.Add(Button("↑", "上一处冲突", () => Navigate(-1)));
        toolbar.Children.Add(Button("↓", "下一处冲突", () => Navigate(1)));
        toolbar.Children.Add(Button("⇐", "当前冲突:采用左侧(我的)", () => Take(false)));
        toolbar.Children.Add(Button("⇒", "当前冲突:采用右侧(对方的)", () => Take(true)));
        var paths = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        paths.Children.Add(_base);
        var mid = _left; Grid.SetColumn(mid, 1); paths.Children.Add(mid);
        var right = _right; Grid.SetColumn(right, 2); paths.Children.Add(right);
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(10), RowSpacing = 8 };
        root.Children.Add(toolbar); Grid.SetRow(paths, 1); root.Children.Add(paths);
        Grid.SetRow(_result, 2); root.Children.Add(_result);
        Grid.SetRow(_status, 3); root.Children.Add(_status);
        Content = root;
        if (GitOutputPath is not null)
            Closing += (_, _) => SaveToOutput();
        Opened += async (_, _) => { if (basePath is not null && leftPath is not null && rightPath is not null) await MergeAsync(); };
    }
    private async Task MergeAsync()
    {
        try
        {
            if (!File.Exists(_base.Text) || !File.Exists(_left.Text) || !File.Exists(_right.Text))
            { _status.Text = "三个路径都必须是存在的文件。"; return; }
            _status.Text = "合并中…";
            string baseText = await File.ReadAllTextAsync(_base.Text!), leftText = await File.ReadAllTextAsync(_left.Text!), rightText = await File.ReadAllTextAsync(_right.Text!);
            static string[] Split(string text) => text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
            var merged = await Task.Run(() => ThreeWayMerger.Merge(Split(baseText), Split(leftText), Split(rightText), "myers", _plugins));
            string newLine = leftText.Contains("\r\n") ? "\r\n" : "\n";
            string composed = merged.Compose(Path.GetFileName(_left.Text!), Path.GetFileName(_right.Text!), newLine);
            _result.Text = composed.Replace("\r\n", "\n");
            Repaint();
        }
        catch (Exception ex) { _status.Text = "合并失败：" + ex.Message; }
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
        _status.Text = _conflicts.Count == 0 ? "合并完成：无冲突，结果可直接保存。" : $"合并完成：{_conflicts.Count} 处冲突,↑↓ 导航,⇐ 采用我的 / ⇒ 采用对方的。";
    }
    private void Navigate(int direction)
    {
        if (_conflicts.Count == 0) { _status.Text = "没有冲突。"; return; }
        _currentConflict = Math.Clamp(_currentConflict + direction, 0, _conflicts.Count - 1);
        var (start, _, end) = _conflicts[_currentConflict];
        _status.Text = $"冲突 {_currentConflict + 1}/{_conflicts.Count}(行 {start + 1}–{end + 1})";
        _result.SelectionStart = _result.Text!.Split('\n')[..start].Aggregate(0, (sum, line) => sum + line.Length + 1);
        _result.SelectionEnd = _result.SelectionStart + _result.Text.Split('\n')[start].Length;
        _result.Focus();
    }
    /// <summary>解决当前冲突:true=采用右侧,false=采用左侧;重写结果文本后重新解析。</summary>
    public void Take(bool useRight)
    {
        if (_currentConflict < 0 || _currentConflict >= _conflicts.Count) { _status.Text = "请先用 ↑↓ 选中一处冲突。"; return; }
        var (start, middle, end) = _conflicts[_currentConflict];
        var lines = (_result.Text ?? "").Split('\n').ToList();
        var chosen = useRight ? lines.GetRange(middle + 1, end - middle - 1) : lines.GetRange(start + 1, middle - start - 1);
        lines.RemoveRange(start, end - start + 1);
        lines.InsertRange(start, chosen);
        _result.Text = string.Join("\n", lines);
        Repaint();
        _status.Text = $"已采用{(useRight ? "右侧" : "左侧")}。剩余 {_conflicts.Count} 处冲突。";
    }
}
