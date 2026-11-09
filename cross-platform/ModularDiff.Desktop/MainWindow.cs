using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Security.Cryptography;
using ModularDiff.Contracts;
using ModularDiff.Core;

namespace ModularDiff.Desktop;
public sealed class MainWindow : Window
{
    private readonly PluginCatalog _plugins;
    private readonly ComparisonSession _session;
    private readonly TextBox _leftPath = new() { Watermark = "左侧文件或目录" }, _rightPath = new() { Watermark = "右侧文件或目录" };
    private readonly TextBox _leftEditor = Editor(), _rightEditor = Editor();
    private readonly TextBlock _status = new() { Text = "选择左右文件或目录后比较", TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _rows = new(), _files = new() { SelectionMode = SelectionMode.Multiple };
    private readonly TabControl _tabs = new();
    private readonly TabItem _diffTab = new() { Header = "差异对齐" };
    private readonly ComboBox _algorithm = new() { Width = 130 };
    private readonly Avalonia.Controls.Primitives.ToggleButton _ignoreEol = new() { Content = "↵", MinWidth = 32, Padding = new Thickness(8, 4) };
    private readonly Avalonia.Controls.Primitives.ToggleButton _ignoreWhitespace = new() { Content = "␣", MinWidth = 32, Padding = new Thickness(8, 4) };
    private readonly Avalonia.Controls.Primitives.ToggleButton _ignoreCase = new() { Content = "Aa", MinWidth = 32, Padding = new Thickness(8, 4) };
    private static int _windowCount;
    private readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly TextBlock _binaryInfo = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14, Foreground = Brush.Parse("#253041"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private ComparisonResult? _directory;
    private bool _loading, _busy, _allowClose, _closePending;
    public MainWindow(string[] args)
    {
        Title = "ModularDiff · 跨平台版"; Width = 1250; Height = 820; MinWidth = 850; MinHeight = 550;
        int cascade = System.Threading.Interlocked.Increment(ref _windowCount) - 1;   // 新窗口级联偏移,避免完全叠加难发现
        Position = new PixelPoint(70 + (cascade % 12) * 36, 46 + (cascade % 12) * 30);
        string? Arg(string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        _plugins = PluginCatalog.Load(Arg("--plugins") ?? DefaultPluginRoot());
        static string DefaultPluginRoot()
        {
            var candidates = new[] { "../plugins", "../Resources/plugins", "../PlugIns" }
                .Select(rel => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, rel)))
                .ToArray();
            return candidates.FirstOrDefault(d => Directory.Exists(d)
                    && Directory.EnumerateFiles(d, "plugin.json", SearchOption.AllDirectories).Any())
                ?? candidates.FirstOrDefault(Directory.Exists)
                ?? candidates[0];
        }
        _session = new(_plugins);
        _leftPath.Text = Arg("--left"); _rightPath.Text = Arg("--right");
        _algorithm.ItemsSource = _plugins.GetAll<IDiffAlgorithm>().Select(p => p.Id).ToArray(); _algorithm.SelectedItem = "myers";
        ToolTip.SetTip(_algorithm, "比较算法插件");
        _toolbar.Children.Add(Button("▶", "比较左右路径", () => Run(Compare)));
        _toolbar.Children.Add(Button("⇄", "交换左右路径", () => Run(async () => { if (!await Leave()) return; _session.Clear(); RefreshEditors(); (_leftPath.Text, _rightPath.Text) = (_rightPath.Text, _leftPath.Text); await Compare(); })));
        _toolbar.Children.Add(_algorithm);
        ToolTip.SetTip(_ignoreEol, "忽略行尾差异（CRLF/LF/CR 与末行换行）"); ToolTip.SetTip(_ignoreWhitespace, "忽略行内所有空白差异"); ToolTip.SetTip(_ignoreCase, "忽略大小写差异（不变文化）");
        _toolbar.Children.Add(_ignoreEol); _toolbar.Children.Add(_ignoreWhitespace); _toolbar.Children.Add(_ignoreCase);
        _ignoreEol.IsCheckedChanged += (_, _) => ApplyOptions(); _ignoreWhitespace.IsCheckedChanged += (_, _) => ApplyOptions(); _ignoreCase.IsCheckedChanged += (_, _) => ApplyOptions();
        _toolbar.Children.Add(Button("↑", "上一个差异块", () => Navigate(-1)));
        _toolbar.Children.Add(Button("↓", "下一个差异块", () => Navigate(1)));
        _toolbar.Children.Add(Button("→", "选中差异块：左复制到右（保存后写入磁盘）", () => Merge(MergeDirection.LeftToRight)));
        _toolbar.Children.Add(Button("←", "选中差异块：右复制到左（保存后写入磁盘）", () => Merge(MergeDirection.RightToLeft)));
        _toolbar.Children.Add(Button("⇥", "目录：预览所选文件左到右同步；未选择时预览全部差异", () => Run(() => Sync(SyncMode.LeftToRight))));
        _toolbar.Children.Add(Button("⇤", "目录：预览所选文件右到左同步；未选择时预览全部差异", () => Run(() => Sync(SyncMode.RightToLeft))));
        _toolbar.Children.Add(Button("◷", "同步历史与撤销恢复", () => Run(History)));
        var paths = Columns(PathPanel(_leftPath), PathPanel(_rightPath));
        _diffTab.Content = _rows;
        var editTab = new TabItem { Header = "左右编辑", Content = Columns(EditPanel(true), EditPanel(false)) };
        var folderTab = new TabItem { Header = "目录比较", Content = _files };
        _tabs.ItemsSource = new[] { _diffTab, editTab, folderTab };
        _rows.ItemTemplate = new FuncDataTemplate<AlignedRow>((row, _) =>
        {
            if (row is null) return new TextBlock();
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
            grid.Children.Add(Cell(row, true)); var right = Cell(row, false); Grid.SetColumn(right, 1); grid.Children.Add(right);
            return new Border { Child = grid };
        });
        _files.ItemTemplate = new FuncDataTemplate<FileComparison>((file, _) =>
            file is null ? new TextBlock() : Columns(
                new TextBlock { Text = file.Status == FileStatus.RightOnly ? "—" : $"{file.Status}  {file.RelativePath}", Margin = new Thickness(4) },
                new TextBlock { Text = file.Status == FileStatus.LeftOnly ? "—" : $"{file.Status}  {file.RelativePath}  {file.Note}", Margin = new Thickness(4) }, false));
        _files.DoubleTapped += (_, _) => Run(async () =>
        {
            if (_directory is null || _files.SelectedItem is not FileComparison file || !await Leave()) return;
            if (file.IsBinary || file.Status == FileStatus.Error) { _status.Text = file.Note ?? "此文件不能作为文本编辑。"; return; }
            await OpenFile(Path.Combine(_directory.LeftPath, file.RelativePath), Path.Combine(_directory.RightPath, file.RelativePath));
        });
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(10), RowSpacing = 8 };
        root.Children.Add(_toolbar); Grid.SetRow(paths, 1); root.Children.Add(paths); Grid.SetRow(_tabs, 2); root.Children.Add(_tabs); Grid.SetRow(_status, 3); root.Children.Add(_status); Content = root;
        _leftEditor.TextChanged += (_, _) => Edited(true); _rightEditor.TextChanged += (_, _) => Edited(false);
        _debounce.Tick += (_, _) => { _debounce.Stop(); try { _session.Recompare(); RefreshRows(); } catch (Exception ex) { _status.Text = ex.Message; } };
        _algorithm.SelectionChanged += (_, _) => { _session.Algorithm = _algorithm.SelectedItem as string ?? "myers"; _rows.ItemsSource = null; _debounce.Start(); };
        Closing += async (_, e) =>
        {
            if (_allowClose) return;
            if (_busy) { e.Cancel = true; _status.Text = "请等待当前操作完成后关闭。"; return; }
            if (!_session.Dirty) return;
            e.Cancel = true; if (_closePending) return; _closePending = true;
            try { if (await Leave()) { _allowClose = true; Close(); } }
            catch (Exception ex) { _status.Text = "保存失败，窗口保持打开：" + ex.Message; }
            finally { _closePending = false; }
        };
        Closed += (_, _) => { _debounce.Stop(); _plugins.Dispose(); System.Threading.Interlocked.Decrement(ref _windowCount); };
        if (_plugins.Diagnostics.Count > 0) { _status.Text = string.Join("\n", _plugins.Diagnostics); _toolbar.IsEnabled = false; }
        else _status.Text = $"已加载 {_plugins.GetAll<IPlugin>().Count} 个插件 · 双击目录条目打开文件";
        if (args.Contains("--auto-compare")) Opened += (_, _) => Run(Compare);
    }
    private static bool SamePath(string? box, string loaded) =>
        !string.IsNullOrWhiteSpace(box) && string.Equals(Path.GetFullPath(box.Trim()), loaded, StringComparison.Ordinal);
    private static TextBox Editor() => new() { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Code,Menlo,DejaVu Sans Mono,monospace"), FontSize = 14, IsReadOnly = true, IsUndoEnabled = false };
    private Control Cell(AlignedRow row, bool left)
    {
        TextCell? cell = left ? row.Left : row.Right;
        // 与 WPF DiffPane 一致的着色:幽灵行灰底,左侧删除/右侧插入/修改各自着色(半透明,选中高亮可透出)。
        string background = cell is null ? "#C8F2F4F7"
            : row.IsDifferent ? (left ? (row.Right is null ? "#B3FFF0F0" : "#B3FFF0E9") : (row.Left is null ? "#B3ECF7EF" : "#B3FFF0E9"))
            : "Transparent";
        var number = new TextBlock { Text = cell is null ? "" : cell.LineNumber.ToString(), Foreground = Brush.Parse("#8793A4"), FontFamily = new FontFamily("Cascadia Code,Menlo,DejaVu Sans Mono,monospace"), FontSize = 12, TextAlignment = TextAlignment.Right, MinWidth = 40, Margin = new Thickness(6, 1, 8, 1), VerticalAlignment = VerticalAlignment.Center };
        var editor = new TextBox { Name = "CellEditor", Text = cell?.Text ?? "", FontFamily = new FontFamily("Cascadia Code,Menlo,DejaVu Sans Mono,monospace"), FontSize = 13, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Margin = new Thickness(0, 1, 6, 1), IsReadOnly = cell is null, AcceptsReturn = false };
        var ending = new TextBlock { Text = cell is { Ending: "CRLF" } ? "⏎CRLF" : cell is { Ending: "CR" } ? "⏎CR" : "", Foreground = Brush.Parse("#8793A4"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        editor.TextChanged += (_, _) => CellEdited(row, left, editor);
        editor.GotFocus += (_, _) => _rows.SelectedItem = row;   // 聚焦单元格即选中所在行,供差异块合并等操作使用
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        line.Children.Add(number); Grid.SetColumn(editor, 1); line.Children.Add(editor); Grid.SetColumn(ending, 2); line.Children.Add(ending);
        return new Border { Background = Brush.Parse(background), MinHeight = 22, Child = line };
    }
    private void CellEdited(AlignedRow row, bool left, TextBox editor)
    {
        if (_loading || _session.Left is null || _session.Right is null) return;
        var cell = left ? row.Left : row.Right;
        if (cell is null) return;
        try
        {
            string[] lines = (left ? _session.Left : _session.Right).Text.Split('\n');
            int index = cell.LineNumber - 1;
            if (index < 0 || index >= lines.Length) return;
            string suffix = lines[index].EndsWith('\r') ? "\r" : "";   // 保留该行原有行尾风格(CRLF)
            if (editor.Text + suffix == lines[index]) return;
            lines[index] = editor.Text + suffix;
            _session.Edit(left, string.Join("\n", lines));
            _debounce.Stop();   // 手动重对齐:编辑时不自动重排,点 ▶ 按当前算法重新对齐
            RefreshDirty();
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
    private static Grid Columns(Control left, Control right, bool resize = true)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,*") }; grid.Children.Add(left); Grid.SetColumn(right, 2); grid.Children.Add(right);
        if (resize) { var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brush.Parse("#DCE2EB") }; Grid.SetColumn(splitter, 1); grid.Children.Add(splitter); } return grid;
    }
    private static Button Button(string icon, string tip, Action action)
    {
        var button = new Button { Content = icon, MinWidth = 32, Padding = new Thickness(8, 4) }; ToolTip.SetTip(button, tip); button.Click += (_, _) => action(); return button;
    }
    private Control PathPanel(TextBox path)
    {
        var panel = new DockPanel { LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        buttons.Children.Add(Button("▤", "选择文件", () => Run(async () => { var items = await StorageProvider.OpenFilePickerAsync(new() { AllowMultiple = false }); if (items.Count > 0) path.Text = items[0].TryGetLocalPath(); })));
        buttons.Children.Add(Button("▣", "选择目录", () => Run(async () => { var items = await StorageProvider.OpenFolderPickerAsync(new() { AllowMultiple = false }); if (items.Count > 0) path.Text = items[0].TryGetLocalPath(); })));
        DockPanel.SetDock(buttons, Dock.Right); panel.Children.Add(buttons); panel.Children.Add(path); return panel;
    }
    private Control EditPanel(bool left)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        bar.Children.Add(new TextBlock { Text = left ? "左侧" : "右侧", VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(Button("▣", "保存此侧（保留编码，检测外部修改冲突）", () => Run(async () => { await _session.SaveAsync(left); RefreshDirty(); })));
        bar.Children.Add(Button("↶", "撤销此侧编辑", () => { try { _session.History(left, false); RefreshEditors(); } catch (Exception ex) { _status.Text = ex.Message; } }));
        bar.Children.Add(Button("↷", "重做此侧编辑", () => { try { _session.History(left, true); RefreshEditors(); } catch (Exception ex) { _status.Text = ex.Message; } }));
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") }; grid.Children.Add(bar); var editor = left ? _leftEditor : _rightEditor; Grid.SetRow(editor, 1); grid.Children.Add(editor); return grid;
    }
    private async void Run(Func<Task> operation)
    {
        if (_busy) return; _busy = true; _toolbar.IsEnabled = false; _tabs.IsEnabled = false; _leftPath.IsEnabled = _rightPath.IsEnabled = false;
        try { await operation(); } catch (Exception ex) { _status.Text = "操作失败：" + ex.Message; }
        finally { _busy = false; _toolbar.IsEnabled = true; _tabs.IsEnabled = true; _leftPath.IsEnabled = _rightPath.IsEnabled = true; }
    }
    private void ApplyOptions()
    {
        _session.Options = new((bool)_ignoreCase.IsChecked!, (bool)_ignoreWhitespace.IsChecked!, (bool)_ignoreEol.IsChecked!);
        if (_session.Left is null) return;
        _rows.ItemsSource = null; _debounce.Stop(); _debounce.Start();
    }
    private async Task Compare()
    {
        // 差异对齐页就地编辑后按 ▶:同路径且未保存时直接内存重对齐,不弹保存对话、不重读磁盘。
        if (_session.Left is not null && _session.Right is not null && _session.Dirty
            && SamePath(_leftPath.Text, _session.Left.Path) && SamePath(_rightPath.Text, _session.Right.Path))
        {
            try { _session.Recompare(); }
            catch (Exception ex) { _status.Text = "重对齐失败：" + ex.Message; return; }
            RefreshRows();
            return;
        }
        if (!await Leave()) return;
        string left = Path.GetFullPath(_leftPath.Text ?? ""), right = Path.GetFullPath(_rightPath.Text ?? "");
        if (string.IsNullOrWhiteSpace(_leftPath.Text) || string.IsNullOrWhiteSpace(_rightPath.Text)) throw new IOException("请指定左右路径。");
        _status.Text = "比较中…";
        var result = await Task.Run(() => new ComparisonEngine(_plugins).CompareAsync(new(left, right, _session.Algorithm, _session.Options)));
        _session.Clear(); RefreshEditors();
        if (Directory.Exists(left) && Directory.Exists(right))
        { _directory = result; _files.ItemsSource = result.Files; _tabs.SelectedIndex = 2; _status.Text = $"{result.Files.Count} 个文件 · {result.Files.Count(f => f.Status != FileStatus.Equal)} 个差异/错误 · 双击打开"; }
        else
        {
            _directory = null; _files.ItemsSource = null;
            var file = result.Files.Single();
            if (file.IsBinary || file.Status == FileStatus.Error)
            {
                _rows.ItemsSource = null; _diffTab.Content = _binaryInfo; _tabs.SelectedIndex = 0;
                _binaryInfo.Text = await BinarySummaryAsync(left, right);
                _status.Text = $"{file.Status}：{file.Note ?? "二进制文件"} · 摘要见「差异对齐」页";
                return;
            }
            await OpenFile(left, right);
        }
        if (result.Warnings.Count > 0) _status.Text += " · " + string.Join("；", result.Warnings);
    }
    private async Task OpenFile(string left, string right) { _diffTab.Content = _rows; await _session.LoadAsync(left, right); RefreshEditors(); _tabs.SelectedIndex = 0; }
    private static async Task<string> BinarySummaryAsync(string left, string right)
    {
        static async Task<(long Size, DateTime Time, string Hash)> Info(string path)
        {
            var file = new FileInfo(path);
            if (!file.Exists) return (0, default, "（缺失）");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return (file.Length, file.LastWriteTime, Convert.ToHexString(await SHA256.HashDataAsync(stream))[..16]);
        }
        var (lSize, lTime, lHash) = await Info(left);
        var (rSize, rTime, rHash) = await Info(right);
        return $"二进制文件，不做文本差异（下方为 SHA-256 前 128 位摘要）\n\n左侧  {left}\n    {lSize:N0} 字节 · {lTime:yyyy-MM-dd HH:mm} · {lHash}\n\n右侧  {right}\n    {rSize:N0} 字节 · {rTime:yyyy-MM-dd HH:mm} · {rHash}\n\n结论：{(lSize == rSize && lHash == rHash ? "两文件内容完全相同" : "两文件内容不同")}";
    }
    private void Edited(bool left)
    {
        if (_loading) return;
        try { if (_session.Left is null) return; string text = (left ? _leftEditor.Text : _rightEditor.Text) ?? ""; if ((left ? _session.Left : _session.Right)?.Text == text) return; _session.Edit(left, text); _rows.ItemsSource = null; _debounce.Stop(); _debounce.Start(); RefreshDirty(); } catch (Exception ex) { _status.Text = ex.Message; }
    }
    private void RefreshEditors()
    {
        _loading = true;
        try { _leftEditor.Text = _session.Left?.Text ?? ""; _rightEditor.Text = _session.Right?.Text ?? ""; _leftEditor.IsReadOnly = _session.Left is null; _rightEditor.IsReadOnly = _session.Right is null; }
        finally { _loading = false; }
        RefreshRows();
    }
    private void RefreshRows() { _rows.ItemsSource = _session.Presentation?.Rows; RefreshDirty(); }
    private void RefreshDirty() => _status.Text = _session.Left is null ? "请选择文件或目录" : $"{(_session.Dirty ? "● 未保存" : "已保存")} · {_session.Presentation?.Hunks.Count ?? 0} 个差异块 · {_session.Left.Path} ↔ {_session.Right?.Path}";
    private void Merge(MergeDirection direction)
    {
        if (_rows.SelectedItem is not AlignedRow { HunkId: >= 0 } row) { _status.Text = "请在差异对齐页选中一个差异块。"; return; }
        try { _debounce.Stop(); _session.Merge(row.HunkId, direction); RefreshEditors(); } catch (Exception ex) { _status.Text = ex.Message; }
    }
    private void Navigate(int direction)
    {
        var rows = _session.Presentation?.Rows; if (rows is null) return;
        int index = _rows.SelectedIndex, current = index >= 0 && index < rows.Count ? rows[index].HunkId : -1;
        for (int i = index < 0 ? (direction > 0 ? 0 : rows.Count - 1) : index + direction; i >= 0 && i < rows.Count; i += direction)
            if (rows[i].HunkId >= 0 && rows[i].HunkId != current) { _tabs.SelectedIndex = 0; _rows.SelectedIndex = i; _rows.ScrollIntoView(rows[i]); return; }
    }
    private async Task<bool> Leave()
    {
        if (!_session.Dirty) return true;
        string choice = await Ask("未保存的修改", "保存左右修改后继续，或放弃修改。", ["保存", "放弃", "取消"]);
        if (choice == "保存") { await _session.SaveAsync(true); await _session.SaveAsync(false); return true; }
        return choice == "放弃";
    }
    private async Task<string> Ask(string title, string text, string[] choices)
    {
        var window = new Window { Title = title, Width = 760, Height = 460, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new Thickness(16) }; var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (string choice in choices) buttons.Children.Add(Button(choice, choice, () => window.Close(choice)));
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons); panel.Children.Add(new ScrollViewer { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } }); window.Content = panel;
        return await window.ShowDialog<string?>(this) ?? "取消";
    }
    private T Plugin<T>() where T : class, IPlugin => _plugins.GetAll<T>().First();
    private SyncSafetyContext Safety() => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModularDiff", "SyncHistory"), Plugin<ISyncBackup>(), Plugin<ISyncJournal>());
    private async Task Sync(SyncMode mode)
    {
        if (_directory is null) throw new IOException("请先比较两个目录。");
        if (!await Leave()) return;
        var selected = _files.SelectedItems?.Cast<FileComparison>().ToArray() ?? [];
        var files = selected.Length > 0 ? selected : _directory.Files.Where(f => f.Status != FileStatus.Equal).ToArray();
        var plan = await Plugin<ISyncPlanner>().PlanAsync(_directory.LeftPath, _directory.RightPath, files.Select(f => f.RelativePath).ToArray(), mode);
        var preview = Plugin<ISyncPreview>().Describe(plan);
        string text = "覆盖前自动备份并记录操作。只执行下面列出的动作：\n\n" + string.Join("\n", preview.Select(p => $"{p.SuggestedAction}  {p.RelativePath}  {p.State}"));
        if (await Ask("目录同步预览", text, ["执行同步", "取消"]) != "执行同步") return;
        var result = await Plugin<ISyncExecutor>().ExecuteAsync(plan, preview.Select(p => new SyncDecision(p.RelativePath, p.SuggestedAction)).ToArray(), Safety());
        _session.Clear(); RefreshEditors();
        _status.Text = $"同步完成：{result.Entries.Count(e => e.Outcome == SyncOutcome.Copied)} 已复制，{result.Entries.Count(e => e.Outcome == SyncOutcome.Failed)} 失败。请重新比较目录刷新结果。";
        await Ask("同步结果", string.Join("\n", result.Entries.Select(e => $"{e.Outcome} {e.RelativePath}: {e.Message}")), ["关闭"]);
    }
    private async Task History()
    {
        var safety = Safety(); var listing = await safety.Journal.ListAsync(safety.StoreRoot);
        if (listing.Operations.Count == 0) { _status.Text = "没有同步记录。" + string.Join("；", listing.Errors); return; }
        var window = new Window { Title = "同步历史 · 双击预览恢复", Width = 850, Height = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var list = new ListBox { ItemsSource = listing.Operations.OrderByDescending(o => o.CreatedUtc).ToArray(), ItemTemplate = new FuncDataTemplate<SyncOperation>((o, _) => new TextBlock { Text = o is null ? "" : $"{o.CreatedUtc.ToLocalTime():g} · {o.State} · {o.LeftRoot} ↔ {o.RightRoot}", Margin = new Thickness(6) }) };
        list.DoubleTapped += (_, _) => { if (list.SelectedItem is SyncOperation operation) window.Close(operation); }; window.Content = list;
        var selected = await window.ShowDialog<SyncOperation?>(this); if (selected is null) return;
        var recovery = Plugin<ISyncRecovery>(); var plan = await recovery.PreviewAsync(safety, selected.Id);
        if (await Ask("撤销恢复预览", string.Join("\n", plan.Items.Select(i => $"{(i.CanRestore ? "可恢复" : "跳过")} {i.RelativePath}: {i.Reason}")), ["恢复可恢复项", "取消"]) != "恢复可恢复项") return;
        if (!await Leave()) return;
        var result = await recovery.RestoreAsync(safety, selected.Id, plan.Items.Where(i => i.CanRestore).Select(i => i.EntryId).ToArray());
        _session.Clear(); RefreshEditors(); _status.Text = $"恢复 {result.Restored}，冲突 {result.Conflicts}，失败 {result.Failed}。请重新比较。";
    }
}
