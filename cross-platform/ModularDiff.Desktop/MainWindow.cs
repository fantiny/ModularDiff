using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Globalization;
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
    private readonly AlignedRowsView _rows = new();
    private readonly ListBox _files = new() { SelectionMode = SelectionMode.Multiple };
    private readonly ScrollViewer _diffScroll = new();
    private readonly Canvas _editorHost = new();
    private readonly TextBox _cellEditor = new() { Name = "CellEditor", FontFamily = new FontFamily("Cascadia Code,Menlo,DejaVu Sans Mono,monospace"), FontSize = 13, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private AlignedRow? _editingRow; private bool _editingLeft, _editorLoading;
    private bool _awaitingAlignTarget; private int _alignSourceLine = -1; private bool _alignSourceIsLeft;
    private readonly List<(Control Target, string Key)> _localized = [];
    private void Tip(Control target, string key) { _localized.Add((target, key)); ToolTip.SetTip(target, Lang.Get(key)); }
    private void ApplyLanguage()
    {
        Title = Lang.Get("md_title");
        _diffTab.Header = Lang.Get("md_tab_diff");
        _diffTab.Header = Lang.Get("md_tab_diff");
        foreach (var (target, key) in _localized) ToolTip.SetTip(target, Lang.Get(key));
    }
    private byte[]? _hexLeft, _hexRight; private string _hexLeftPath = "", _hexRightPath = ""; private bool _hexDirty;
    private bool HexMode => _hexLeft is not null;
    private readonly TabControl _tabs = new();
    private readonly TabItem _diffTab = new() { Header = "差异对齐" };
    private readonly ComboBox _algorithm = new() { Width = 130 };
    private readonly Avalonia.Controls.Primitives.ToggleButton _ignoreEol = new() { Content = "↵", MinWidth = 32, Padding = new Thickness(8, 4) };
    private readonly Avalonia.Controls.Primitives.ToggleButton _ignoreWhitespace = new() { Content = "␣", MinWidth = 32, Padding = new Thickness(8, 4) };
    private readonly Avalonia.Controls.Primitives.ToggleButton _ignoreCase = new() { Content = "Aa", MinWidth = 32, Padding = new Thickness(8, 4) };
    private readonly Avalonia.Controls.Primitives.ToggleButton _autoRealign = new() { Content = "⚡", MinWidth = 32, Padding = new Thickness(8, 4) };
    private readonly ComboBox _sessionBox = new() { Width = 170 }, _recentBox = new() { Width = 170 };
    private SessionStore.SessionFile _sessions = SessionStore.Load();
    private IReadOnlyList<string> _unimportant = [];
    private static int _windowCount;
    private readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly TextBlock _binaryInfo = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14, Foreground = Brush.Parse("#253041"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private ComparisonResult? _directory;
    private bool _lastFocusLeft = true;
    private bool _loading, _busy, _allowClose, _closePending;
    public MainWindow(string[] args)
    {
        Title = Lang.Get("md_title"); Width = 1250; Height = 820; MinWidth = 850; MinHeight = 550;
        int cascade = System.Threading.Interlocked.Increment(ref _windowCount) - 1;   // 新窗口级联偏移,避免完全叠加难发现
        Position = new PixelPoint(70 + (cascade % 12) * 36, 46 + (cascade % 12) * 30);
        string? Arg(string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        _plugins = App.Plugins ?? PluginCatalog.Load(Arg("--plugins") ?? ResolvePluginRoot());   // shared when App is active; self-load in tests
        _session = new(_plugins);
        _leftPath.Text = Arg("--left"); _rightPath.Text = Arg("--right");
        _algorithm.ItemsSource = _plugins.GetAll<IDiffAlgorithm>().Select(p => p.Id).ToArray(); _algorithm.SelectedItem = "myers";
        Tip(_algorithm, "md_tip_algo");
        _toolbar.Children.Add(Button("▶", Lang.Get("md_tip_compare"), () => Run(Compare)));
        _toolbar.Children.Add(Button("⟳", Lang.Get("md_tip_realign"), Realign));
        ToolTip.SetTip(_autoRealign, "自动重对齐：右键换行等格式化操作后立即重算对齐；关闭时需点 ⟳ 手动刷新（打字编辑始终不自动重排）");
        _toolbar.Children.Add(_autoRealign);
        _toolbar.Children.Add(Button("◱", Lang.Get("md_tip_layout"), () => { _rows.Vertical = !_rows.Vertical; _status.Text = _rows.Vertical ? "布局：上下分栏。" : "布局：左右并排。"; }));
        _toolbar.Children.Add(Button("⌗", Lang.Get("md_tip_format"), FormatSide));
        _toolbar.Children.Add(Button("◈", Lang.Get("md_tip_rules"), EditRules));
        _toolbar.Children.Add(Button("💾", Lang.Get("md_tip_session"), () => Run(SaveSession)));
        Button themeButton = new() { Content = Palette.IsDark ? "☀" : "🌙", MinWidth = 32, Padding = new Thickness(8, 4) };
        Tip(themeButton, "md_tip_theme");
        themeButton.Click += (_, _) =>
        {
            Palette.SetDark(!Palette.IsDark);
            RequestedThemeVariant = Palette.IsDark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
            themeButton.Content = Palette.IsDark ? "☀" : "🌙";
        };
        _toolbar.Children.Add(themeButton);
        var languageBox = new ComboBox { Width = 96 };
        languageBox.ItemsSource = Lang.Available.ToArray();
        languageBox.SelectedItem = Lang.CurrentName;
        languageBox.SelectionChanged += (_, _) =>
        {
            if (languageBox.SelectedItem is string name && !string.Equals(name, Lang.CurrentName))
            {
                Lang.Apply(name);
                Palette.SaveLanguage(name);
                ApplyLanguage();
                _status.Text = string.Format(Lang.Get("md_lang_switched"), name);
            }
        };
        Tip(languageBox, "md_tip_lang");
        _toolbar.Children.Add(languageBox);
        ToolTip.SetTip(_sessionBox, "命名会话：选择后载入并比较"); ToolTip.SetTip(_recentBox, "最近比较（自动记录，去重，最多 10 条）");
        _sessionBox.SelectionChanged += (_, _) => Run(async () => { if (_sessionBox.SelectedIndex >= 0 && _sessions.Named.Count > _sessionBox.SelectedIndex) await LoadSession(_sessions.Named[_sessionBox.SelectedIndex]); _sessionBox.SelectedIndex = -1; });
        _recentBox.SelectionChanged += (_, _) => Run(async () => { if (_recentBox.SelectedIndex >= 0 && _sessions.Recent.Count > _recentBox.SelectedIndex) await LoadSession(_sessions.Recent[_recentBox.SelectedIndex]); _recentBox.SelectedIndex = -1; });
        _toolbar.Children.Add(_sessionBox); _toolbar.Children.Add(_recentBox); RefreshSessions();
        _toolbar.Children.Add(Button("⇄", Lang.Get("md_tip_swap"), () => Run(async () => { if (!await Leave()) return; _session.Clear(); RefreshEditors(); (_leftPath.Text, _rightPath.Text) = (_rightPath.Text, _leftPath.Text); await Compare(); })));
        _toolbar.Children.Add(_algorithm);
        Tip(_ignoreEol, "md_tip_eol"); Tip(_ignoreWhitespace, "md_tip_ws"); Tip(_ignoreCase, "md_tip_case");
        _toolbar.Children.Add(_ignoreEol); _toolbar.Children.Add(_ignoreWhitespace); _toolbar.Children.Add(_ignoreCase);
        _ignoreEol.IsCheckedChanged += (_, _) => ApplyOptions(); _ignoreWhitespace.IsCheckedChanged += (_, _) => ApplyOptions(); _ignoreCase.IsCheckedChanged += (_, _) => ApplyOptions();
        _toolbar.Children.Add(Button("↑", Lang.Get("md_tip_prev"), () => Navigate(-1)));
        _toolbar.Children.Add(Button("↓", Lang.Get("md_tip_next"), () => Navigate(1)));
        _toolbar.Children.Add(Button("→", Lang.Get("md_tip_merge_l2r"), () => Merge(MergeDirection.LeftToRight)));
        _toolbar.Children.Add(Button("←", Lang.Get("md_tip_merge_r2l"), () => Merge(MergeDirection.RightToLeft)));
        _toolbar.Children.Add(Button("⇉", Lang.Get("md_tip_copyall_l2r"), () => Run(() => CopyAllBlocks(true))));
        _toolbar.Children.Add(Button("⇇", Lang.Get("md_tip_copyall_r2l"), () => Run(() => CopyAllBlocks(false))));
        _toolbar.Children.Add(Button("⇥", Lang.Get("md_tip_sync_l2r"), () => Run(() => Sync(SyncMode.LeftToRight))));
        _toolbar.Children.Add(Button("⇤", Lang.Get("md_tip_sync_r2l"), () => Run(() => Sync(SyncMode.RightToLeft))));
        _toolbar.Children.Add(Button("◷", Lang.Get("md_tip_history"), () => Run(History)));
        var paths = Columns(PathPanel(_leftPath), PathPanel(_rightPath));
        _diffTab.Header = Lang.Get("md_tab_diff");
        _diffTab.Content = _diffScroll; _diffScroll.Content = new Grid { Children = { _rows, _editorHost } };
        _editorHost.Children.Add(_cellEditor); _cellEditor.IsVisible = false;
        _cellEditor.TextChanged += (_, _) => { if (_editingRow is { } row && !_editorLoading) CellEdited(row, _editingLeft, _cellEditor); };
        _cellEditor.KeyDown += (_, e) => { if (e.Key is Avalonia.Input.Key.Enter or Avalonia.Input.Key.Escape) { if (e.Key == Avalonia.Input.Key.Escape) _editorLoading = true; EndCellEdit(); e.Handled = true; } };
        _cellEditor.LostFocus += (_, _) => EndCellEdit();
        _rows.EditRequested += (row, left) =>
        {
            if (_awaitingAlignTarget) { CompleteAlign(row, left); return; }
            if (HexMode && !left) { _status.Text = "十六进制编辑仅支持左侧。"; return; }
            BeginEditRow(_rows.IndexOfRow(row), left);
        };
        _rows.MenuRequested += (row, left) =>
        {
            var menu = new ContextMenu();
            if (_session.Presentation?.Hunks.Count > 0)
            {
                var allRight = new MenuItem { Header = Lang.Get("md_menu_copy_all_l") };
                allRight.Click += (_, _) => Run(() => CopyAllBlocks(true));
                var allLeft = new MenuItem { Header = Lang.Get("md_menu_copy_all_r") };
                allLeft.Click += (_, _) => Run(() => CopyAllBlocks(false));
                menu.Items.Add(allRight); menu.Items.Add(allLeft); menu.Items.Add(new Separator());
            }
            string selected = _rows.SelectedText();
            if (selected.Length > 0)
            {
                var copy = new MenuItem { Header = Lang.Get("md_menu_copy_selection") };
                copy.Click += async (_, _) => { if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(selected); };
                menu.Items.Add(copy);
            }
            if (_session.Left is not null)
            {
                var lineCopy = new MenuItem { Header = Lang.Get("md_menu_copy_line") };
                lineCopy.Click += (_, _) => Run(async () => { _rows.SelectRows([row]); CopyToSide(row); });
                var selectionCopy = new MenuItem { Header = Lang.Get("md_menu_copy_sel_to_side") };
                selectionCopy.Click += (_, _) => Run(async () => CopyToSide());
                var section = new MenuItem { Header = Lang.Get("md_menu_select_hunk") };
                section.Click += (_, _) => _rows.SelectHunk(row);
                var goTo = new MenuItem { Header = Lang.Get("md_menu_goto_line") };
                goTo.Click += (_, _) => Run(GoToLineAsync);
                var transforms = new MenuItem { Header = $"转换（{(left ? "左" : "右")}侧）" };
                foreach (var (name, kind) in new[] { ("修剪行尾空白", 0), ("Tab → 空格", 1), ("前导空格 → Tab", 2), ("行尾 LF ⇄ CRLF", 3) })
                {
                    int chosen = kind;
                    var item = new MenuItem { Header = name };
                    item.Click += (_, _) => Run(async () => TransformSide(chosen));
                    transforms.Items.Add(item);
                }
                menu.Items.Add(lineCopy); menu.Items.Add(selectionCopy); menu.Items.Add(section); menu.Items.Add(goTo);
                menu.Items.Add(transforms); menu.Items.Add(new Separator());
            }
            if (HexMode)
            {
                var gotoOffset = new MenuItem { Header = Lang.Get("md_menu_goto_offset") };
                gotoOffset.Click += (_, _) => Run(GoToOffsetAsync);
                var patch = new MenuItem { Header = Lang.Get("md_menu_export_patch") };
                patch.Click += (_, _) => Run(ExportHexPatchAsync);
                var saveHex = new MenuItem { Header = Lang.Get("md_menu_save_hex") + (_hexDirty ? " ●" : "") };
                saveHex.Click += (_, _) => Run(async () => SaveHexEdit());
                menu.Items.Add(gotoOffset); menu.Items.Add(patch); menu.Items.Add(saveHex);
                menu.Items.Add(new Separator());
            }
            if (!HexMode && _session.Left is not null && _session.Right is not null)
            {
                var align = new MenuItem { Header = Lang.Get("md_menu_align") };
                align.Click += (_, _) => Run(async () => StartAlignWith(row, left));
                var isolate = new MenuItem { Header = Lang.Get("md_menu_isolate") };
                isolate.Click += (_, _) => Run(async () => IsolateSelection());
                var clearAlign = new MenuItem { Header = Lang.Get("md_menu_clear_align") };
                clearAlign.Click += (_, _) => Run(async () => ClearAlignmentHints());
                menu.Items.Add(align); menu.Items.Add(isolate); menu.Items.Add(clearAlign);
                menu.Items.Add(new Separator());
                foreach (var submenu in FileTypeMenus(row, left))
                    menu.Items.Add(submenu);
            }
            if (CreateSplitMenu(row, left) is { } split)
                foreach (var item in split.Items.OfType<MenuItem>()) menu.Items.Add(item);
            if (menu.ItemCount > 0) _rows.OpenMenu(menu);
        };
        var editTab = new TabItem { Header = Lang.Get("md_tab_edit"), Content = Columns(EditPanel(true), EditPanel(false)) };
        var folderTab = new TabItem { Header = Lang.Get("md_tab_folder"), Content = _files };
        _tabs.ItemsSource = new[] { _diffTab, editTab, folderTab };
        _files.ItemTemplate = new FuncDataTemplate<FileComparison>((file, _) =>
            file is null ? new TextBlock() : Columns(
                new TextBlock { Text = file.Status == FileStatus.RightOnly ? "—" : $"{file.Status}  {file.RelativePath}", Margin = new Thickness(4) },
                new TextBlock { Text = file.Status == FileStatus.LeftOnly ? "—" : $"{file.Status}  {file.RelativePath}  {file.Note}", Margin = new Thickness(4) }, false));
        _files.AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(_files).Properties.PointerUpdateKind != Avalonia.Input.PointerUpdateKind.RightButtonReleased) return;
            var selected = _files.SelectedItems?.Cast<FileComparison>().ToArray() ?? [];
            if (selected.Length == 0) return;
            var menu = new ContextMenu();
            var toRight = new MenuItem { Header = $"复制 {selected.Length} 个文件到右侧" };
            toRight.Click += (_, _) => Run(() => CopySelectedFolderFiles(true));
            var toLeft = new MenuItem { Header = $"复制 {selected.Length} 个文件到左侧" };
            toLeft.Click += (_, _) => Run(() => CopySelectedFolderFiles(false));
            menu.Items.Add(toRight); menu.Items.Add(toLeft);
            _files.ContextMenu = menu;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
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
        _algorithm.SelectionChanged += (_, _) => { _session.Algorithm = _algorithm.SelectedItem as string ?? "myers"; _rows.Rows = []; _debounce.Start(); };
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
        this.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.G && (e.KeyModifiers & (Avalonia.Input.KeyModifiers.Meta | Avalonia.Input.KeyModifiers.Control)) != 0)
            { e.Handled = true; Run(GoToLineAsync); }
            if (e.Key == Avalonia.Input.Key.Escape && _awaitingAlignTarget)
            { _awaitingAlignTarget = false; _status.Text = "已取消手动对齐。"; e.Handled = true; }
        });
        if (Palette.IsDark) RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        if (Palette.SavedLanguage is { } saved && Lang.Available.Contains(saved)) Lang.Apply(saved);
        ApplyLanguage();
        // 声明式文件(语法文法/语言包)加载失败要报告而不是静默跳过 —— 逐文件给出行列与原因。
        if (_plugins.GetAll<ISyntaxGrammar>().SelectMany(g => g.LoadDiagnostics).Concat(Lang.LoadDiagnostics).ToList() is { Count: > 0 } issues)
        {
            _status.Text = $"⚠ {issues.Count} 个声明式文件加载失败（语法/语言包）· 其余正常";
            Opened += (_, _) => Run(async () => await Ask("声明式文件诊断",
                "以下文件已跳过，其余正常加载：\n\n" + string.Join("\n", issues), ["关闭"]));
        }
        if (args.Contains("--auto-compare")) Opened += (_, _) => Run(Compare);
    }
    /// <summary>Fallback plugin root when App hasn't initialized (headless tests).</summary>
    private static string ResolvePluginRoot()
    {
        var candidates = new[] { "../plugins", "../Resources/plugins", "../PlugIns" }
            .Select(rel => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, rel)))
            .ToArray();
        return candidates.FirstOrDefault(d => Directory.Exists(d)
                && Directory.EnumerateFiles(d, "plugin.json", SearchOption.AllDirectories).Any())
            ?? candidates.FirstOrDefault(Directory.Exists)
            ?? candidates[0];
    }

    private static bool SamePath(string? box, string loaded) =>
        !string.IsNullOrWhiteSpace(box) && string.Equals(Path.GetFullPath(box.Trim()), loaded, StringComparison.Ordinal);
    private static TextBox Editor() => new() { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Code,Menlo,DejaVu Sans Mono,monospace"), FontSize = 14, IsReadOnly = true, IsUndoEnabled = false };
    /// <summary>按点击侧构建换行菜单;批量数按当前选中集合实时统计(右键时懒构建,不再有陈旧菜单)。</summary>
    public ContextMenu? CreateSplitMenu(AlignedRow row, bool left)
    {
        var cell = left ? row.Left : row.Right;
        if (cell is null) return null;
        string path = (left ? _session.Left : _session.Right)?.Path ?? _leftPath.Text ?? "";
        var splitters = _plugins.GetAll<ILineSplitter>().Where(s => s.CanSplit(path, cell.Text)).ToArray();
        if (splitters.Length == 0) return null;
        var menu = new ContextMenu();
        foreach (var splitter in splitters)
        {
            var chosen = splitter;
            int batch = CountSplittable(left, chosen);
            var item = new MenuItem { Header = batch > 1 ? $"换行 · {chosen.DisplayName}（{batch} 行）" : "换行 · " + chosen.DisplayName, Tag = Math.Max(1, batch) };
            item.Click += (_, _) => { if (item.Tag is int n && n > 1) ApplySplitBatch(left, chosen); else ApplySplit(row, left, chosen); };
            menu.Items.Add(item);
        }
        return menu;
    }
    /// <summary>转换(0 修剪行尾空白 / 1 Tab→空格 / 2 前导空格→Tab / 3 行尾 LF⇄CRLF),作用于最近聚焦侧。</summary>
    public void TransformSide(int kind)
    {
        if (_session.Left is null || _session.Right is null) { _status.Text = "请先打开文本比较。"; return; }
        bool left = _lastFocusLeft;
        string text = (left ? _session.Left : _session.Right).Text;
        try
        {
            string result = kind switch
            {
                0 => string.Join("\n", text.Split('\n').Select(l => l.TrimEnd(' ', '\t', '\r'))),
                1 => text.Replace("\t", new string(' ', 4)),
                2 => string.Join("\n", text.Split('\n').Select(l => { int spaces = l.TakeWhile(c => c == ' ').Count(); return new string('\t', spaces / 4) + l[Math.Min(spaces - spaces % 4, l.Length)..]; })),
                _ => text.Contains("\r\n") ? text.Replace("\r\n", "\n") : text.Replace("\n", "\r\n"),
            };
            if (result == text) { _status.Text = "内容无变化。"; return; }
            _session.Edit(left, result);
            _debounce.Stop(); RefreshDirty();
            string name = kind == 0 ? "修剪行尾空白" : kind == 1 ? "Tab → 空格" : kind == 2 ? "前导空格 → Tab" : "行尾风格切换";
            if ((bool)_autoRealign.IsChecked!) Realign();
            else _status.Text = $"已{name}（{(left ? "左" : "右")}侧）· 点 ⟳ 重新对齐";
        }
        catch (Exception ex) { _status.Text = "转换失败：" + ex.Message; }
    }
    private async Task GoToLineAsync()
    {
        if (_rows.RowCount == 0) { _status.Text = "请先打开文本比较。"; return; }
        string? input = await Prompt("转到行号", $"行号（{( _lastFocusLeft ? "左" : "右")}侧，当前聚焦）", "1");
        if (!int.TryParse(input, out int line) || line < 1) return;
        bool left = _lastFocusLeft;
        for (int i = 0; i < _rows.RowCount; i++)
            if ((left ? _rows.Rows[i].Left : _rows.Rows[i].Right)?.LineNumber == line)
            {
                _rows.SelectRows([_rows.Rows[i]]); _rows.EnsureVisible(i); _rows.Focus();
                return;
            }
        _status.Text = $"行号 {line} 不在{(left ? "左" : "右")}侧。";
    }
    /// <summary>把一行(或文本选区覆盖的行)复制到对侧:按对齐重建目标侧,幽灵位置落为插入,行尾沿用目标风格。</summary>
    public void CopyToSide(AlignedRow? row = null, bool? fromLeft = null)
    {
        if (_session.Left is null || _session.Right is null) { _status.Text = "请先打开文本比较。"; return; }
        var range = _rows.TextSelectionRange;
        int fromRow, toRow;
        if (range is { } r) { fromRow = r.FromRow; toRow = r.ToRow; }
        else
        {
            row ??= _rows.SelectedRows.FirstOrDefault();
            if (row is null) { _status.Text = "请先选中要复制的行。"; return; }
            int index = _rows.IndexOfRow(row);
            if (index < 0) return;
            fromRow = toRow = index;
        }
        bool sourceLeft = fromLeft ?? range?.Left ?? _lastFocusLeft;
        bool usesCrLf = (sourceLeft ? _session.Right : _session.Left).Text.Contains("\r\n");
        var target = new List<string>();
        int copied = 0;
        for (int i = 0; i < _rows.RowCount; i++)
        {
            var current = _rows.Rows[i];
            bool inSelection = i >= fromRow && i <= toRow;
            var sourceCell = sourceLeft ? current.Left : current.Right;
            var targetCell = sourceLeft ? current.Right : current.Left;
            if (inSelection && sourceCell is not null)
            {
                target.Add(usesCrLf ? sourceCell.Text : sourceCell.Text);
                copied++;
            }
            else if (targetCell is not null) target.Add(targetCell.Text);
            // 对侧为幽灵且不在选区:目标本就没有这一行,保持没有。
        }
        _session.Edit(!sourceLeft, string.Join("\n", target));
        _debounce.Stop(); RefreshDirty();
        if ((bool)_autoRealign.IsChecked!) Realign();
        else _status.Text = $"已复制 {copied} 行到{(sourceLeft ? "右" : "左")}侧 · 点 ⟳ 重新对齐";
    }
    // ─── Align With / Isolate(引擎已支持:AlignmentHint + isolatedLines)───
    private void StartAlignWith(AlignedRow row, bool left)
    {
        var cell = left ? row.Left : row.Right;
        if (cell is null) { _status.Text = "此侧没有行可对齐。"; return; }
        _alignSourceLine = cell.LineNumber;
        _alignSourceIsLeft = left;
        _awaitingAlignTarget = true;
        _status.Text = $"对齐模式：请在另一侧点击目标行（Esc 取消）。源：{(left ? "左" : "右")}侧行 {cell.LineNumber}";
    }
    private void CompleteAlign(AlignedRow target, bool targetLeft)
    {
        _awaitingAlignTarget = false;
        if (targetLeft == _alignSourceIsLeft) { _status.Text = "目标必须是另一侧的行。"; return; }
        var targetCell = targetLeft ? target.Left : target.Right;
        if (targetCell is null || _alignSourceLine < 1) { _status.Text = "目标行无效，请重新开始对齐。"; return; }
        int leftLine = targetLeft ? targetCell.LineNumber : _alignSourceLine;
        int rightLine = targetLeft ? _alignSourceLine : targetCell.LineNumber;
        _session.AlignmentHints = [.. _session.AlignmentHints, new(leftLine, rightLine)];
        _session.Recompare(); RefreshRows();
        _status.Text = $"已手动对齐：左行 {leftLine} ↔ 右行 {rightLine}（共 {_session.AlignmentHints.Count} 条提示）";
    }
    private void IsolateSelection()
    {
        var rows = _rows.SelectedRows;
        if (rows.Count == 0) { _status.Text = "请先选中要隔离的行。"; return; }
        var leftLines = new List<int>(_session.IsolatedLeftLines);
        var rightLines = new List<int>(_session.IsolatedRightLines);
        foreach (var row in rows)
        {
            if (row.Left is { } l && !leftLines.Contains(l.LineNumber)) leftLines.Add(l.LineNumber);
            if (row.Right is { } r && !rightLines.Contains(r.LineNumber)) rightLines.Add(r.LineNumber);
        }
        _session.IsolatedLeftLines = leftLines; _session.IsolatedRightLines = rightLines;
        _session.Recompare(); RefreshRows();
        _status.Text = $"已隔离 {rows.Count} 行（左 {leftLines.Count} / 右 {rightLines.Count}）";
    }
    /// <summary>测试/程序化入口:添加手动对齐提示并立即重排。</summary>
    public void AddAlignmentHint(int leftLine, int rightLine)
    {
        _session.AlignmentHints = [.. _session.AlignmentHints, new(leftLine, rightLine)];
        _session.Recompare(); RefreshRows();
    }
    /// <summary>测试/程序化入口:列出指定侧可用的文件类型操作标签。</summary>
    public IReadOnlyList<string> FileTypeActionLabels(bool left)
    {
        string extension = Path.GetExtension((left ? _session.Left : _session.Right)?.Path ?? "");
        var providers = _plugins.GetAll<IFileTypeProvider>()
            .Where(p => p.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)).ToList();
        if (providers.Count == 0)   // 无专属提供者 → 通配 "*" 的通用文件工具
            providers = _plugins.GetAll<IFileTypeProvider>().Where(p => p.Extensions.Contains("*", StringComparer.Ordinal)).ToList();
        return providers.SelectMany(p => p.EditActions.Select(a => p.LanguageName + ":" + a.Label)).ToList();
    }
    /// <summary>测试/程序化入口:执行指定侧第 index 个文件类型操作(按 FileTypeActionLabels 顺序)。</summary>
    public void RunFileTypeAction(bool left, int index)
    {
        string extension = Path.GetExtension((left ? _session.Left : _session.Right)?.Path ?? "");
        var actions = _plugins.GetAll<IFileTypeProvider>()
            .Where(p => p.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            .SelectMany(p => p.EditActions).ToList();
        if (index < 0 || index >= actions.Count) return;
        var buffer = left ? _session.Left : _session.Right;
        if (buffer is null) return;
        string result = actions[index].Transform(new(buffer, 1, 1));
        if (string.IsNullOrEmpty(result) || result == buffer.Text) { _status.Text = "内容无变化。"; return; }
        _session.Edit(left, result);
        _debounce.Stop(); RefreshDirty();
        if ((bool)_autoRealign.IsChecked!) Realign();
    }
    private void ClearAlignmentHints()
    {
        _session.AlignmentHints = []; _session.IsolatedLeftLines = []; _session.IsolatedRightLines = [];
        _session.Recompare(); RefreshRows();
        _status.Text = "已清除全部手动对齐与隔离。";
    }
    // ─── 文件类型插件(IFileTypeProvider)右键子菜单:按点击侧扩展名给出语言专属操作 ───
    private IEnumerable<MenuItem> FileTypeMenus(AlignedRow row, bool left)
    {
        string path = (left ? _session.Left : _session.Right)?.Path ?? "";
        string extension = Path.GetExtension(path);
        bool anySpecific = _plugins.GetAll<IFileTypeProvider>().Any(p => p.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
        foreach (var provider in _plugins.GetAll<IFileTypeProvider>())
        {
            bool wildcard = provider.Extensions.Contains("*", StringComparer.Ordinal);
            if (wildcard && anySpecific) continue;                 // 专属提供者优先;通配只在无专属时出现
            if (!wildcard && !provider.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) continue;
            var capturedProvider = provider;
            var submenu = new MenuItem { Header = $"{capturedProvider.LanguageName} 工具（{(left ? "左" : "右")}侧）" };
            foreach (var (label, _, transform) in capturedProvider.EditActions)
            {
                var action = transform;
                var item = new MenuItem { Header = label };
                item.Click += (_, _) => Run(async () =>
                {
                    var buffer = left ? _session.Left : _session.Right;
                    if (buffer is null) return;
                    try
                    {
                        string result = action(new(buffer, (left ? _rows.SelectedRows.FirstOrDefault()?.Left : _rows.SelectedRows.FirstOrDefault()?.Right)?.LineNumber ?? 1, 1));
                        if (string.IsNullOrEmpty(result) || result == buffer.Text) { _status.Text = "内容无变化。"; return; }
                        _session.Edit(left, result);
                        _debounce.Stop(); RefreshDirty();
                        if ((bool)_autoRealign.IsChecked!) Realign();
                        else _status.Text = $"已执行「{label}」· 点 ⟳ 重新对齐";
                    }
                    catch (Exception ex) { _status.Text = $"{label} 失败：{ex.Message}"; }
                });
                submenu.Items.Add(item);
            }
            yield return submenu;
        }
    }
    // ─── 十六进制:偏移导航 / 补丁导出 / 就地编辑保存 ───
    private async Task GoToOffsetAsync()
    {
        if (!HexMode) return;
        string? input = await Prompt("转到字节偏移", "十六进制(0x…)或十进制", "0");
        if (string.IsNullOrWhiteSpace(input)) return;
        string trimmed = input.Trim();
        if (!long.TryParse(trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? trimmed[2..] : trimmed,
            System.Globalization.NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long offset)
            && !long.TryParse(trimmed, out offset))
        { _status.Text = "无法解析偏移。"; return; }
        long max = Math.Max(_hexLeft!.Length, _hexRight!.Length);
        if (offset < 0 || offset >= max) { _status.Text = $"偏移超出范围 [0, {max})。"; return; }
        int index = (int)(offset / 16);
        _rows.SelectRows([_rows.Rows[index]]); _rows.EnsureVisible(index);
        _status.Text = $"已定位偏移 0x{offset:X}（行 {index + 1}）。";
    }
    private async Task ExportHexPatchAsync()
    {
        if (!HexMode) return;
        var a = _hexLeft!; var b = _hexRight!;
        var report = new System.Text.StringBuilder();
        report.AppendLine("ModularDiff Binary Patch Report");
        report.AppendLine($"Left:  {_hexLeftPath}  ({a.Length} bytes)");
        report.AppendLine($"Right: {_hexRightPath}  ({b.Length} bytes)");
        int segments = 0;
        for (int i = 0; i < Math.Max(a.Length, b.Length); )
        {
            bool differs = i < a.Length && i < b.Length ? a[i] != b[i] : true;
            if (!differs) { i++; continue; }
            int start = i;
            while (i < Math.Max(a.Length, b.Length) && (i >= a.Length || i >= b.Length || a[i] != b[i])) i++;
            segments++;
            report.AppendLine($"- segment {segments}: offset 0x{start:X} length {i - start}");
            report.AppendLine("  left : " + BitConverter.ToString(a, Math.Min(start, a.Length == 0 ? 0 : start), Math.Min(i - start, Math.Max(0, a.Length - start))).Replace('-', ' '));
            report.AppendLine("  right: " + BitConverter.ToString(b, Math.Min(start, b.Length == 0 ? 0 : start), Math.Min(i - start, Math.Max(0, b.Length - start))).Replace('-', ' '));
        }
        report.AppendLine(segments == 0 ? "Files are identical." : $"{segments} differing segment(s).");
        var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = "modulardiff-binary.patch", DefaultExtension = "patch" });
        if (target is null) return;
        await File.WriteAllTextAsync(target.TryGetLocalPath() ?? "modulardiff-binary.patch", report.ToString());
        _status.Text = $"补丁报告已导出（{segments} 段差异）。";
    }
    public void SaveHexEdit()
    {
        if (!HexMode) return;
        if (!_hexDirty) { _status.Text = "没有未保存的十六进制修改。"; return; }
        try
        {
            File.WriteAllBytes(_hexLeftPath, _hexLeft!);
            _hexDirty = false;
            _status.Text = "十六进制编辑已写入磁盘，重新比较…";
            Run(Compare);
        }
        catch (Exception ex) { _status.Text = "保存失败：" + ex.Message; }
    }
    private void FormatSide()
    {
        if (_loading || _session.Left is null || _session.Right is null) return;
        bool left = _lastFocusLeft;
        var buffer = left ? _session.Left : _session.Right;
        var formatter = _plugins.GetAll<ICodeFormatter>().FirstOrDefault(f => f.CanFormat(buffer.Path));
        if (formatter is null) { _status.Text = $"没有支持 {Path.GetFileName(buffer.Path)} 的格式化插件（当前插件：{string.Join("、", _plugins.GetAll<ICodeFormatter>().Select(f => f.DisplayName))}）。"; return; }
        try
        {
            string suffix = buffer.Text.Contains("\r\n") ? "\r" : "";   // 行尾风格沿用原文件
            var formatted = formatter.Format(buffer.Text.Split('\n').Select(l => l.TrimEnd('\r')).ToList());
            _session.Edit(left, string.Join("\n" + suffix, formatted) + suffix);
            _debounce.Stop();
            RefreshDirty();
            if ((bool)_autoRealign.IsChecked!) Realign();
            else _status.Text = $"已按「{formatter.DisplayName}」格式化{(left ? "左" : "右")}侧 · 点 ⟳ 重新对齐";
        }
        catch (Exception ex) { _status.Text = "格式化失败：" + ex.Message; }
    }
    private void Realign()
    {
        if (_session.Left is null || _session.Right is null) { Run(Compare); return; }
        try { _session.Recompare(); RefreshRows(); }
        catch (Exception ex) { _status.Text = "重对齐失败：" + ex.Message; }
    }
    private int CountSplittable(bool left, ILineSplitter splitter)
    {
        string path = (left ? _session.Left : _session.Right)?.Path ?? _leftPath.Text ?? "";
        return _rows.SelectedRows
            .Select(r => left ? r.Left : r.Right).OfType<TextCell>()
            .Count(c => splitter.CanSplit(path, c.Text));
    }
    private void ApplySplitBatch(bool left, ILineSplitter splitter)
    {
        if (_loading || _session.Left is null || _session.Right is null) return;
        string[] lines = (left ? _session.Left : _session.Right).Text.Split('\n');
        var targets = _rows.SelectedRows
            .Select(r => left ? r.Left : r.Right).OfType<TextCell>()
            .Where(c => c.LineNumber - 1 < lines.Length)
            .OrderByDescending(c => c.LineNumber).ToList();   // 自底向上替换,行号不失效
        if (targets.Count == 0) return;
        int applied = 0;
        foreach (var cell in targets)
        {
            string raw = lines[cell.LineNumber - 1];
            string suffix = raw.EndsWith('\r') ? "\r" : "";
            var parts = splitter.Split(suffix.Length > 0 ? raw[..^1] : raw);
            if (parts.Count < 2) continue;
            lines[cell.LineNumber - 1] = string.Join("\n" + suffix, parts) + suffix;
            applied++;
        }
        if (applied == 0) return;
        _session.Edit(left, string.Join("\n", lines));
        _debounce.Stop();
        RefreshDirty();
        if ((bool)_autoRealign.IsChecked!) Realign();
        else _status.Text = $"已按「{splitter.DisplayName}」换行 {applied} 行 · 点 ⟳ 重新对齐";
    }
    private void ApplySplit(AlignedRow row, bool left, ILineSplitter splitter)
    {
        if (_loading || _session.Left is null || _session.Right is null) return;
        var cell = left ? row.Left : row.Right;
        if (cell is null) return;
        try
        {
            string[] lines = (left ? _session.Left : _session.Right).Text.Split('\n');
            int index = cell.LineNumber - 1;
            if (index < 0 || index >= lines.Length) return;
            string raw = lines[index];
            string suffix = raw.EndsWith('\r') ? "\r" : "";   // 行尾风格(CRLF)沿用原行
            var parts = splitter.Split(suffix.Length > 0 ? raw[..^1] : raw);
            if (parts.Count < 2) return;
            lines[index] = string.Join("\n" + suffix, parts) + suffix;
            _session.Edit(left, string.Join("\n", lines));
            _debounce.Stop();   // 手动模式:点 ⟳ 重对齐;⚡ 开启时立即重算
            RefreshDirty();
            if ((bool)_autoRealign.IsChecked!) Realign();
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
    /// <summary>测试/交互入口:在指定行指定侧弹出行内编辑器(与视图点击同一通路)。</summary>
    public void BeginEditRow(int index, bool left)
    {
        if (index < 0 || index >= _rows.RowCount) return;
        var row = _rows.Rows[index];
        var cell = left ? row.Left : row.Right;
        if (cell is null) return;
        if (_editingRow is not null) EndCellEdit();   // 换单元格前提交上一个
        _editingRow = row; _editingLeft = left; _lastFocusLeft = left;
        _editorLoading = true; _cellEditor.Text = cell.Text; _editorLoading = false;
        var rect = _rows.CellRect(index, left);
        _cellEditor.Width = rect.Width; _cellEditor.Height = AlignedRowsView.RowHeight;
        Canvas.SetLeft(_cellEditor, rect.X); Canvas.SetTop(_cellEditor, rect.Y);
        _cellEditor.IsVisible = true;
        _cellEditor.Focus(); _cellEditor.CaretIndex = _cellEditor.Text.Length;
        _rows.SelectRows([row]);
    }
    private void EndCellEdit()
    {
        if (_editingRow is null) return;
        if (_editorLoading) { _cellEditor.Text = ""; _editorLoading = false; }   // Esc:还原显示
        else if (_cellEditor.IsFocused || true) { /* TextChanged 已实时提交 */ }
        _editingRow = null;
        _cellEditor.IsVisible = false;
    }
    private void CellEdited(AlignedRow row, bool left, TextBox editor)
    {
        if (HexMode) { HexCellEdited(row, editor); return; }
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
        _session.Options = BuildOptions();
        if (_session.Left is null) return;
        _rows.Rows = []; _debounce.Stop(); _debounce.Start();
    }
    private ComparisonOptions BuildOptions() => new((bool)_ignoreCase.IsChecked!, (bool)_ignoreWhitespace.IsChecked!, (bool)_ignoreEol.IsChecked!, UnimportantPatterns: _unimportant);
    private void RefreshSessions()
    {
        _sessionBox.ItemsSource = _sessions.Named.Select(s => $"📌 {s.Name} · {Path.GetFileName(s.Left)} ↔ {Path.GetFileName(s.Right)}").ToArray();
        _recentBox.ItemsSource = _sessions.Recent.Select(s => $"🕘 {Path.GetFileName(s.Left)} ↔ {Path.GetFileName(s.Right)}").ToArray();
    }
    private async Task LoadSession(SavedSession session)
    {
        if (!await Leave()) return;
        _leftPath.Text = session.Left; _rightPath.Text = session.Right;
        _algorithm.SelectedItem = session.Algorithm ?? "myers";
        _ignoreCase.IsChecked = session.IgnoreCase; _ignoreWhitespace.IsChecked = session.IgnoreWhitespace; _ignoreEol.IsChecked = session.IgnoreEol;
        _unimportant = session.UnimportantPatterns ?? [];
        _session.Algorithm = session.Algorithm ?? "myers";
        _session.Options = BuildOptions();
        await Compare();
    }
    private async Task SaveSession()
    {
        if (string.IsNullOrWhiteSpace(_leftPath.Text) || string.IsNullOrWhiteSpace(_rightPath.Text)) { _status.Text = "请先指定左右路径。"; return; }
        string? name = await Prompt("保存会话", "会话名称", $"{Path.GetFileName(_leftPath.Text)} ↔ {Path.GetFileName(_rightPath.Text)}");
        if (string.IsNullOrWhiteSpace(name)) return;
        _sessions = _sessions.UpsertNamed(new(name, _leftPath.Text.Trim(), _rightPath.Text.Trim(), _session.Algorithm,
            (bool)_ignoreCase.IsChecked!, (bool)_ignoreWhitespace.IsChecked!, (bool)_ignoreEol.IsChecked!, true, DateTime.Now, _unimportant));
        SessionStore.Save(_sessions);
        RefreshSessions();
        _status.Text = $"会话「{name}」已保存。";
    }
    private async void EditRules()
    {
        var window = new Window { Title = "不重要差异规则", Width = 600, Height = 340, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = true };
        var input = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Code,Menlo,DejaVu Sans Mono,monospace"), Text = string.Join("\n", _unimportant), Watermark = "每行一条正则，匹配到的文本不算差异。例：\\$Id:.*\\$　\\d{4}-\\d{2}-\\d{2}　v\\d+\\.\\d+", Margin = new Thickness(12), MinHeight = 180 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        var panel = new DockPanel { Margin = new Thickness(6) };
        foreach (var (label, _) in new[] { ("应用", ""), ("清空", ""), ("取消", "") })
        {
            string choice = label;
            buttons.Children.Add(Button(choice, choice, () => window.Close(choice == "取消" ? null : choice == "清空" ? "" : input.Text ?? "")));
        }
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons); panel.Children.Add(input);
        window.Content = panel;
        string? result = await window.ShowDialog<string?>(this);
        if (result is null) return;
        var patterns = result.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        foreach (string pattern in patterns)
        {
            try { _ = new System.Text.RegularExpressions.Regex(pattern); }
            catch (ArgumentException ex) { _status.Text = $"规则无效：{pattern}（{ex.Message}）"; return; }
        }
        _unimportant = patterns;
        ToolTip.SetTip(_algorithm, _unimportant.Count > 0 ? $"比较算法插件 · {_unimportant.Count} 条不重要规则" : "比较算法插件");
        ApplyOptions();
        _status.Text = $"已应用 {_unimportant.Count} 条不重要差异规则。";
    }
    private async Task<string?> Prompt(string title, string label, string defaultValue)
    {
        var window = new Window { Title = title, Width = 480, Height = 170, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var input = new TextBox { Text = defaultValue, Watermark = label, Margin = new Thickness(12) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        buttons.Children.Add(Button("保存", label, () => window.Close(input.Text)));
        buttons.Children.Add(Button("取消", label, () => window.Close(null)));
        var panel = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons); panel.Children.Add(input);
        window.Content = panel;
        return await window.ShowDialog<string?>(this);
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
            if (result.ModuleId == "pictures" && file.Status != FileStatus.Error)
            {
                ShowPicture(left, right, file);
                _status.Text = $"图片比较 · {file.Note}";
                RecordRecent(left, right);
                return;
            }
            if (file.IsBinary || file.Status == FileStatus.Error)
            {
                _rows.Rows = []; _tabs.SelectedIndex = 0;
                string summary = await BinarySummaryAsync(left, right);
                if (file.Status == FileStatus.Error) { _diffTab.Content = _binaryInfo; _binaryInfo.Text = summary; _status.Text = $"{file.Status}：{file.Note}"; return; }
                ShowHex(left, right, file);
                string verdict = summary.Split('\n').LastOrDefault(l => l.TrimStart().StartsWith("结论")) ?? "";
                _status.Text = $"十六进制视图（16 字节/行，着色行含差异字节）· {verdict.Trim()}";
                RecordRecent(left, right);
                return;
            }
            await OpenFile(left, right);
        }
        RecordRecent(left, right);
        if (result.Warnings.Count > 0) _status.Text += " · " + string.Join("；", result.Warnings);
    }
    private void RecordRecent(string left, string right)
    {
        _sessions = _sessions.RecordRecent(left, right, _session.Algorithm,
            (bool)_ignoreCase.IsChecked!, (bool)_ignoreWhitespace.IsChecked!, (bool)_ignoreEol.IsChecked!, true, _unimportant);
        SessionStore.Save(_sessions);
        RefreshSessions();
    }
    private async Task OpenFile(string left, string right)
    {
        _diffTab.Header = Lang.Get("md_tab_diff");
        _diffTab.Content = _diffScroll; _hexLeft = null; _hexDirty = false;
        await _session.LoadAsync(left, right);
        WireSyntax(left, right);
        RefreshEditors(); _tabs.SelectedIndex = 0;
    }
    /// <summary>按扩展名把 IFileTypeProvider.HighlightLine 接到视图两侧的语法着色。</summary>
    private void WireSyntax(string leftPath, string rightPath)
    {
        _rows.SyntaxLeft = SyntaxFor(leftPath);
        _rows.SyntaxRight = SyntaxFor(rightPath);
    }
    private Func<string, IReadOnlyList<SyntaxToken>?>? SyntaxFor(string path)
    {
        string extension = Path.GetExtension(path);
        // 与 WPF 同优先级链:声明式语法文法插件 → 文件类型插件 HighlightLine → 无着色。
        var grammar = _plugins.GetAll<ISyntaxGrammar>().FirstOrDefault(g => g.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
        if (grammar is not null) return line => grammar.Tokenize(line, extension);
        var provider = _plugins.GetAll<IFileTypeProvider>().FirstOrDefault(p => p.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
        return provider is null ? null : line => provider.HighlightLine(line);
    }
    /// <summary>十六进制视图:16 字节/行并排 dump,与任一差异字节段重叠的行着色,自动定位首个差异行(单侧最多 1MB)。</summary>
    private void ShowHex(string left, string right, FileComparison file)
    {
        const int cap = 1 << 20;
        static byte[] Read(string path)
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[Math.Min(stream.Length, cap)];
            stream.ReadAtLeast(buffer, buffer.Length);
            return buffer;
        }
        var a = Read(left); var b = Read(right);
        _hexLeft = a; _hexRight = b; _hexLeftPath = left; _hexRightPath = right; _hexDirty = false;
        _diffTab.Header = Lang.Get("md_tab_diff");
        _diffTab.Content = _diffScroll;
        _rows.Rows = BuildHexRows(a, b);
        int first = FirstHexDifferenceRow(a, b);
        if (first >= 0) { _rows.SelectRows([_rows.Rows[first]]); _rows.EnsureVisible(first); }
    }
    private const int HexRowBytes = 16;
    private static List<AlignedRow> BuildHexRows(byte[] a, byte[] b)
    {
        int rows = (int)((Math.Max(a.Length, b.Length) + HexRowBytes - 1) / HexRowBytes);
        var list = new List<AlignedRow>(rows);
        for (int row = 0; row < rows; row++)
        {
            long offset = (long)row * HexRowBytes;
            bool differs = false;
            for (int i = 0; i < HexRowBytes && !differs; i++)
            {
                long at = offset + i;
                differs = at >= a.Length || at >= b.Length || a[at] != b[at];
            }
            list.Add(new(row, new(row + 1, DumpHex(a, offset), "LF", []), new(row + 1, DumpHex(b, offset), "LF", []), differs ? 0 : -1));
        }
        return list;
    }
    private static int FirstHexDifferenceRow(byte[] a, byte[] b)
    {
        for (long at = 0; at < Math.Max(a.Length, b.Length); at++)
            if (at >= a.Length || at >= b.Length || a[at] != b[at]) return (int)(at / HexRowBytes);
        return -1;
    }
    private static string DumpHex(byte[] buffer, long offset)
    {
        var hex = new System.Text.StringBuilder(HexRowBytes * 3);
        var ascii = new System.Text.StringBuilder(HexRowBytes);
        for (int i = 0; i < HexRowBytes; i++)
        {
            if (offset + i < buffer.Length)
            {
                byte value = buffer[offset + i];
                hex.Append(value.ToString("x2")).Append(' ');
                ascii.Append(value is >= 0x20 and < 0x7F ? (char)value : '·');
            }
            else { hex.Append("   "); ascii.Append(' '); }
        }
        return hex.ToString() + ' ' + ascii;
    }
    /// <summary>十六进制就地编辑:单元格文本按"每 token 两个十六进制位"解析,须与该行字节数一致;ASCII 尾列不参与。</summary>
    private void HexCellEdited(AlignedRow row, TextBox editor)
    {
        if (_loading || row.Left is not { } cell) return;
        try
        {
            long offset = (long)(cell.LineNumber - 1) * HexRowBytes;
            byte[] leftBytes = _hexLeft ?? throw new InvalidOperationException("十六进制模式未初始化。");
            int rowBytes = (int)Math.Min(HexRowBytes, Math.Max(0, leftBytes.Length - offset));
            if (rowBytes == 0) return;
            var tokens = (editor.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length == 2 && Uri.IsHexDigit(t[0]) && Uri.IsHexDigit(t[1])).Take(rowBytes + 1).ToList();
            if (tokens.Count != rowBytes)
            {
                _status.Text = $"需恰好 {rowBytes} 个十六进制字节(如 4d 5a …)。";
                _editorLoading = true; editor.Text = DumpHex(leftBytes, offset); _editorLoading = false;
                return;
            }
            for (int i = 0; i < rowBytes; i++) leftBytes[offset + i] = Convert.ToByte(tokens[i], 16);
            _hexDirty = true;
            int editedRow = cell.LineNumber - 1;
            _rows.Rows = BuildHexRows(leftBytes, _hexRight ?? []);
            _rows.SelectRows([_rows.Rows[editedRow]]); _rows.EnsureVisible(editedRow);
            _status.Text = $"已修改第 {editedRow + 1} 行的 {rowBytes} 个字节 · 右键「保存十六进制编辑」写盘";
        }
        catch (Exception ex) { _status.Text = "十六进制编辑失败：" + ex.Message; }
    }
    /// <summary>图片并排视图:尺寸一致(且 ≤4096×4096)时右侧叠加半透明红色差异像素(SkiaSharp 解码,无图像库落盘)。</summary>
    private void ShowPicture(string left, string right, FileComparison file)
    {
        static Avalonia.Media.IImage? Load(string path) { try { return new Avalonia.Media.Imaging.Bitmap(path); } catch { return null; } }
        var panel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 8, Margin = new Thickness(8) };
        panel.Children.Add(new TextBlock { Text = file.Note ?? "", Foreground = Brush.Parse("#253041"), TextWrapping = TextWrapping.Wrap });
        var images = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var overlay = OverlayDifference(left, right);
        Control Pane(Avalonia.Media.IImage? image, string title, Avalonia.Media.IImage? overlayImage)
        {
            var header = new TextBlock { Text = title + (overlayImage is null ? "" : "（红色叠层 = 差异像素）"), Foreground = Brush.Parse("#8793A4"), Margin = new Thickness(0, 4) };
            Control content;
            if (image is null)
                content = new TextBlock { Text = "无法解码", Foreground = Brush.Parse("#A52D22") };
            else if (overlayImage is null)
                content = new Image { Source = image, Stretch = Stretch.Uniform, MaxHeight = 480, HorizontalAlignment = HorizontalAlignment.Left };
            else
            {
                var stack = new Grid();
                stack.Children.Add(new Image { Source = image, Stretch = Stretch.Uniform, MaxHeight = 480, HorizontalAlignment = HorizontalAlignment.Left });
                stack.Children.Add(new Image { Source = overlayImage, Stretch = Stretch.Uniform, MaxHeight = 480, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false });
                content = stack;
            }
            return new StackPanel { Children = { header, content } };
        }
        images.Children.Add(Pane(Load(left), "左侧", null));
        var rightPane = Pane(Load(right), "右侧", overlay);
        Grid.SetColumn(rightPane, 1);
        images.Children.Add(rightPane);
        panel.Children.Add(images);
        _diffTab.Content = panel;
    }
    private static Avalonia.Media.IImage? OverlayDifference(string left, string right)
    {
        try
        {
            using var l = SkiaSharp.SKBitmap.Decode(left);
            using var r = SkiaSharp.SKBitmap.Decode(right);
            if (l is null || r is null || l.Width != r.Width || l.Height != r.Height) return null;
            if ((long)l.Width * l.Height > 4096L * 4096L) return null;
            using var a = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(l.Width, l.Height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul));
            using var b = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(r.Width, r.Height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul));
            if (!l.CopyTo(a) || !r.CopyTo(b)) return null;
            var overlay = new Avalonia.Media.Imaging.WriteableBitmap(new Avalonia.PixelSize(l.Width, l.Height), new Avalonia.Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888);
            // 差异掩码 + 3×3 膨胀:单像素差异缩放后不足 2 设备像素,膨胀一圈才看得清(仅显示层)。
            var differs = new bool[l.Width, l.Height];
            unsafe
            {
                for (int y = 0; y < l.Height; y++)
                {
                    byte* rowA = (byte*)a.GetAddress(0, y);
                    byte* rowB = (byte*)b.GetAddress(0, y);
                    for (int x = 0; x < l.Width; x++)
                        differs[x, y] = rowA[x * 4] != rowB[x * 4] || rowA[x * 4 + 1] != rowB[x * 4 + 1] || rowA[x * 4 + 2] != rowB[x * 4 + 2] || rowA[x * 4 + 3] != rowB[x * 4 + 3];
                }
            }
            const byte alpha = 150;
            byte blue = (byte)(40 * alpha / 255), green = (byte)(30 * alpha / 255), red = (byte)(255 * alpha / 255);   // 预乘 alpha(Avalonia 位图要求)
            using (var locked = overlay.Lock())
            {
                unsafe
                {
                    byte* target = (byte*)locked.Address;
                    for (int y = 0; y < l.Height; y++)
                    {
                        byte* rowO = target + y * locked.RowBytes;
                        for (int x = 0; x < l.Width; x++)
                        {
                            rowO[x * 4] = 0; rowO[x * 4 + 1] = 0; rowO[x * 4 + 2] = 0; rowO[x * 4 + 3] = 0;
                            bool near = false;
                            for (int dy = -1; dy <= 1 && !near; dy++)
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    int nx = x + dx, ny = y + dy;
                                    if (nx >= 0 && nx < l.Width && ny >= 0 && ny < l.Height && differs[nx, ny]) { near = true; break; }
                                }
                            if (near) { rowO[x * 4] = blue; rowO[x * 4 + 1] = green; rowO[x * 4 + 2] = red; rowO[x * 4 + 3] = alpha; }
                        }
                    }
                }
            }
            return overlay;
        }
        catch { return null; }
    }
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
        try { if (_session.Left is null) return; string text = (left ? _leftEditor.Text : _rightEditor.Text) ?? ""; if ((left ? _session.Left : _session.Right)?.Text == text) return; _session.Edit(left, text); _rows.Rows = []; _debounce.Stop(); _debounce.Start(); RefreshDirty(); } catch (Exception ex) { _status.Text = ex.Message; }
    }
    private void RefreshEditors()
    {
        _loading = true;
        try { _leftEditor.Text = _session.Left?.Text ?? ""; _rightEditor.Text = _session.Right?.Text ?? ""; _leftEditor.IsReadOnly = _session.Left is null; _rightEditor.IsReadOnly = _session.Right is null; }
        finally { _loading = false; }
        RefreshRows();
    }
    private void RefreshRows() { _rows.Rows = _session.Presentation?.Rows ?? []; RefreshDirty(); }
    private void RefreshDirty() => _status.Text = _session.Left is null ? "请选择文件或目录" : $"{(_session.Dirty ? "● 未保存" : "已保存")} · {_session.Presentation?.Hunks.Count ?? 0} 个差异块 · {_session.Left.Path} ↔ {_session.Right?.Path}";
    private async Task CopyAllBlocks(bool toRight)
    {
        if (_session.Left is null || _session.Right is null) { _status.Text = "请先打开文本比较。"; return; }
        try
        {
            int copied = _session.MergeAll(toRight ? MergeDirection.LeftToRight : MergeDirection.RightToLeft);
            RefreshEditors();
            _status.Text = copied == 0 ? "没有可复制的差异块。" : $"已复制 {copied} 个差异块到{(toRight ? "右侧" : "左侧")} · 未保存，保存后写入磁盘";
        }
        catch (Exception ex) { _status.Text = "批量复制失败：" + ex.Message; }
    }
    private void Merge(MergeDirection direction)
    {
        if (_rows.SelectedRows.FirstOrDefault(r => r.HunkId >= 0) is not { } row) { _status.Text = "请在差异对齐页选中一个差异块。"; return; }
        try { _debounce.Stop(); _session.Merge(row.HunkId, direction); RefreshEditors(); } catch (Exception ex) { _status.Text = ex.Message; }
    }
    private void Navigate(int direction)
    {
        var rows = _rows.Rows; if (rows.Count == 0) return;
        int index = _rows.PrimarySelected, current = index >= 0 && index < rows.Count ? rows[index].HunkId : -1;
        for (int i = index < 0 ? (direction > 0 ? 0 : rows.Count - 1) : index + direction; i >= 0 && i < rows.Count; i += direction)
            if (rows[i].HunkId >= 0 && rows[i].HunkId != current) { _tabs.SelectedIndex = 0; _rows.SelectRows([rows[i]]); _rows.EnsureVisible(i); return; }
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
    /// <summary>测试/程序化入口:按相对路径选中目录页文件(等价于用户在列表中多选)。</summary>
    public void SelectFolderFiles(params string[] relativePaths)
    {
        if (_files.ItemsSource is not IReadOnlyList<FileComparison> files) return;
        _files.SelectedItems?.Clear();
        foreach (var path in relativePaths)
        {
            var match = files.FirstOrDefault(f => f.RelativePath == path);
            if (match is not null) _files.SelectedItems?.Add(match);
        }
    }
    /// <summary>目录页批量复制所选文件到另一侧:缺源跳过、逐文件容错、完成后重比较并恢复选中。</summary>
    public async Task CopySelectedFolderFiles(bool fromLeft)
    {
        if (_directory is null) { _status.Text = "请先比较两个目录。"; return; }
        var selected = _files.SelectedItems?.Cast<FileComparison>().ToArray() ?? [];
        if (selected.Length == 0) return;
        string reopen = (_files.SelectedItem as FileComparison ?? selected[0]).RelativePath;
        int copied = 0, failed = 0;
        foreach (var file in selected)
        {
            try
            {
                string source = Path.Combine(fromLeft ? _directory.LeftPath : _directory.RightPath, file.RelativePath);
                string target = Path.Combine(fromLeft ? _directory.RightPath : _directory.LeftPath, file.RelativePath);
                if (!File.Exists(source)) { failed++; continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
                copied++;
            }
            catch (Exception) { failed++; }
        }
        await Compare();
        var restored = (_files.ItemsSource as IReadOnlyList<FileComparison>)?.FirstOrDefault(f => f.RelativePath == reopen);
        if (restored is not null) _files.SelectedItem = restored;
        _status.Text = $"批量复制完成：{copied} 成功" + (failed > 0 ? $"，{failed} 失败/跳过" : "") + "。";
    }
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
