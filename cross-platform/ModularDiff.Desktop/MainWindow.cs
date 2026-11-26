using Avalonia;
using Avalonia.Automation;
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
public sealed partial class MainWindow : Window
{

    private readonly PluginCatalog _plugins;
    private readonly bool _ownsPlugins;
    private readonly ComparisonSession _session;
    private readonly TextBox _leftPath = new() { Watermark = Lang.Get("md_path_left_hint") }, _rightPath = new() { Watermark = Lang.Get("md_path_right_hint") };
    private readonly TextBox _leftEditor = Editor(), _rightEditor = Editor();
    // Each edit panel is built once, so the side captions are kept where a language switch can find them.
    private readonly TextBlock _leftSideLabel = new() { Text = Lang.Get("side_left"), VerticalAlignment = VerticalAlignment.Center },
        _rightSideLabel = new() { Text = Lang.Get("side_right"), VerticalAlignment = VerticalAlignment.Center };
    private readonly StatusLine _status = new(new TextBlock { Text = Lang.Get("status_initial"), TextWrapping = TextWrapping.Wrap });
    // 状态栏若正显示"语言已切换"的通知,记着它的配方:再切一次要换成这一次的语言,而不是把上一次的通知按新语言重新渲染。
    private Func<string>? _languageNotice;
    // 启动提示("已加载 N 个插件…")同理:切语言时让位给切换通知 —— 它只是一句提示,而通知确认了刚才的操作。
    private Func<string>? _startupNotice;
    private readonly AlignedRowsView _rows = new();
    private readonly ListBox _files = new() { SelectionMode = SelectionMode.Multiple };
    private readonly ScrollViewer _diffScroll = new();
    private readonly Canvas _editorHost = new();
    // ─── 查找:差异视图两侧的行文本。查找条在差异页上方,⌘F 打开;F3 / ⇧F3 下一处 / 上一处,Enter 在查找框里是下一处,Esc 关闭 ───
    private readonly Grid _diffHost = new();
    private readonly Grid _findBar = new() { IsVisible = false, ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto,Auto"), Margin = new Thickness(6, 4, 6, 2) };
    private readonly TextBox _findBox = new() { Watermark = Lang.Get("a11y_find"), Width = 380, MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _findCount = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), MinWidth = 64 };
    private readonly Avalonia.Controls.Primitives.ToggleButton _findCase = new() { Content = "Aa", Padding = new Thickness(6, 2) };
    private readonly TextBox _cellEditor = new() { Name = "CellEditor", FontFamily = new FontFamily("Cascadia Code,Menlo,DejaVu Sans Mono,monospace"), FontSize = 13, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private AlignedRow? _editingRow; private bool _editingLeft, _editorLoading;
    // 打开着的编辑框上一次写进缓冲区的文字(按它自己的写法):打开时是那一行,粘贴进来几行就是几行。
    private string _cellCommitted = "";
    private bool _awaitingAlignTarget; private int _alignSourceLine = -1; private bool _alignSourceIsLeft;
    private readonly List<(Control Target, string Key)> _localized = [];
    // 提示文字会随界面语言变化,不能当控件的标识;自动化标识固定为语言键,测试据此查找控件。
    private void Tip(Control target, string key)
    {
        _localized.Add((target, key));
        AutomationProperties.SetAutomationId(target, key);
        ToolTip.SetTip(target, Lang.Get(key));
    }
    private Button LButton(string icon, string tipKey, Action action)
    {
        var button = Button(icon, "", action);
        Tip(button, tipKey);
        return button;
    }
    private string AlgorithmTip() => _unimportant.Count > 0 ? Lang.Get("md_algo_box_tip", Lang.Get("md_tip_algo"), _unimportant.Count) : Lang.Get("md_tip_algo");
    private void ApplyLanguage()
    {
        Title = Lang.Get("md_title");
        _diffTab.Header = Lang.Get("md_tab_diff");
        _editTab.Header = Lang.Get("md_tab_edit");      // 三个 tab 头全部纳入语言切换(此前编辑/目录页永不更新)
        _folderTab.Header = Lang.Get("md_tab_folder");
        foreach (var (target, key) in _localized) ToolTip.SetTip(target, Lang.Get(key));
        ToolTip.SetTip(_algorithm, AlgorithmTip());   // names the unimportant rules too, when there are any
        _leftPath.Watermark = Lang.Get("md_path_left_hint"); _rightPath.Watermark = Lang.Get("md_path_right_hint");
        _leftSideLabel.Text = Lang.Get("side_left"); _rightSideLabel.Text = Lang.Get("side_right");
        _findBox.Watermark = Lang.Get("a11y_find"); UpdateFindCount();
        LocalizeFolderView();   // 筛选下拉/计数/已显示的行跟着换语言
        // 页签标题变了长短,页签条要重新测量:否则切到更长的语言时标题被截成 "Aligned ["。
        int selected = Math.Max(0, _tabs.SelectedIndex);
        _tabs.ItemsSource = null; _tabs.ItemsSource = new[] { _diffTab, _editTab, _folderTab }; _tabs.SelectedIndex = selected;
        RefreshSessions();     // 会话 / 最近比较 两个框的标题跟着换语言
        RefreshFormatBox();    // 报告格式的名字是插件名的译文,也跟着换
        _status.Relocalize();   // 用语言键生成的状态文本跟着换语言
    }
    /// <summary>状态栏。直接赋值的文本是"说过的原话";用 <see cref="Localized"/> 设置的文本带一份配方,
    /// 切换界面语言时按新语言重新生成。两类互斥 —— 直接赋值会清掉配方,不会把过期的配方重新渲染出来。</summary>
    internal sealed class StatusLine(TextBlock block)
    {
        private Func<string>? _recipe;
        public TextBlock Block => block;
        public bool HasRecipe => _recipe is not null;
        /// <summary>The recipe the text is rendered from, when it has one.</summary>
        public Func<string>? Recipe => _recipe;
        public string? Text { get => block.Text; set { _recipe = null; block.Text = value; } }
        public void Localized(Func<string> recipe) { _recipe = recipe; block.Text = recipe(); }
        public void Relocalize() { if (_recipe is not null) block.Text = _recipe(); }
    }
    private byte[]? _hexLeft, _hexRight; private string _hexLeftPath = "", _hexRightPath = ""; private bool _hexDirty;
    private bool HexMode => _hexLeft is not null;
    private readonly TabControl _tabs = new();
    private readonly TabItem _diffTab = new() { Header = Lang.Get("md_tab_diff") }, _editTab = new(), _folderTab = new();
    private readonly ComboBox _languageBox = new() { Width = 96 };
    private readonly ComboBox _algorithm = new() { Width = 130 };
    // 报告导出:格式全部来自 IResultFormatter 插件(文本 / JSON / HTML)。下拉里显示的是插件名的译文,换语言时重建。
    private readonly ComboBox _formatBox = new() { Width = 150 };
    private IResultFormatter[] _formatters = [];
    private readonly Avalonia.Controls.Primitives.ToggleButton _ignoreEol = new() { Content = "↵", MinWidth = 32, Padding = new Thickness(8, 4) };
    private readonly Avalonia.Controls.Primitives.ToggleButton _ignoreWhitespace = new() { Content = "␣", MinWidth = 32, Padding = new Thickness(8, 4) };
    private readonly Avalonia.Controls.Primitives.ToggleButton _ignoreCase = new() { Content = "Aa", MinWidth = 32, Padding = new Thickness(8, 4) };
    private readonly Avalonia.Controls.Primitives.ToggleButton _autoRealign = new() { Content = "⚡", MinWidth = 32, Padding = new Thickness(8, 4) };
    private readonly ComboBox _sessionBox = new() { Width = 170 }, _recentBox = new() { Width = 170 };
    private SessionStore.SessionFile _sessions = SessionStore.Load();
    private IReadOnlyList<string> _unimportant = [];
    private static int _windowCount;
    // 可换行:25 个控件共需约 1340px,默认窗口放不下一行。此前用不换行的 StackPanel,最右侧的按钮被窗口
    // 边缘切掉、中间的下拉框被挤成空白。WrapPanel 在不够宽时折到下一行。
    private readonly WrapPanel _toolbar = new() { Name = "MainToolbar", Orientation = Orientation.Horizontal };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly TextBlock _binaryInfo = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14, Foreground = Brush.Parse("#253041"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private ComparisonResult? _directory;
    /// <summary>最近一次直接比较的文件对(十六进制 / 图片 / 出错的那些没有文本会话);导出报告时用它。
    /// 目录比较与打开文本对都会清掉它。</summary>
    private ComparisonResult? _pair;
    private bool _lastFocusLeft = true;
    private bool _loading, _busy, _allowClose, _closePending;
    [Flags]
    private enum EditTyping { None = 0, Left = 1, Right = 2 }
    private EditTyping _typingIn;   // a box of the edit tab took input that its TextChanged has not committed yet
    private int _compareRun;   // 进度回调所属的比较轮次:结果已应用(或已开始新一轮)后,迟到的进度更新不再改写状态栏
    public MainWindow(string[] args)
    {
        Title = Lang.Get("md_title"); Width = 1250; Height = 820; MinWidth = 850; MinHeight = 550;
        int cascade = System.Threading.Interlocked.Increment(ref _windowCount) - 1;   // 新窗口级联偏移,避免完全叠加难发现
        Position = new PixelPoint(70 + (cascade % 12) * 36, 46 + (cascade % 12) * 30);
        string? Arg(string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        _ownsPlugins = App.Plugins is null;   // 共享目录归 App 管(多窗口复用);仅自加载时窗口才负责 Dispose
        _plugins = App.Plugins ?? PluginCatalog.Load(Arg("--plugins") ?? ResolvePluginRoot());   // shared when App is active; self-load in tests
        _session = new(_plugins);
        _leftPath.Text = Arg("--left"); _rightPath.Text = Arg("--right");
        _algorithm.ItemsSource = _plugins.GetAll<IDiffAlgorithm>().Select(p => p.Id).ToArray(); _algorithm.SelectedItem = FirstAlgorithmId();
        _session.Algorithm = FirstAlgorithmId();   // A3 收尾:构造期 SelectionChanged 尚未挂,须显式同步会话(否则目录首个算法≠myers 时显示与实际不符)
        Tip(_algorithm, "md_tip_algo");
        _toolbar.Children.Add(LButton("▶", "md_tip_compare", () => Run(Compare)));
        _toolbar.Children.Add(LButton("⟳", "md_tip_realign", Realign));
        Tip(_autoRealign, "md_tip_auto");
        _toolbar.Children.Add(_autoRealign);
        _toolbar.Children.Add(LButton("◱", "md_tip_layout", () => { _rows.Vertical = !_rows.Vertical; _status.Text = Lang.Get(_rows.Vertical ? "status_layout_vertical" : "status_layout_horizontal"); }));
        _toolbar.Children.Add(LButton("⌗", "md_tip_format", FormatSide));
        _toolbar.Children.Add(LButton("◈", "md_tip_rules", EditRules));
        _toolbar.Children.Add(LButton("💾", "md_tip_session", () => Run(SaveSession)));
        Button themeButton = new() { Content = Palette.IsDark ? "☀" : "🌙", MinWidth = 32, Padding = new Thickness(8, 4) };
        Tip(themeButton, "md_tip_theme");
        themeButton.Click += (_, _) =>
        {
            Palette.SetDark(!Palette.IsDark);
            RequestedThemeVariant = Palette.IsDark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
            themeButton.Content = Palette.IsDark ? "☀" : "🌙";
        };
        _toolbar.Children.Add(themeButton);
        var languageBox = _languageBox;
        languageBox.ItemsSource = Lang.Available.ToArray();
        languageBox.SelectedItem = Lang.CurrentName;
        languageBox.SelectionChanged += (_, _) =>
        {
            if (languageBox.SelectedItem is string name && !string.Equals(name, Lang.CurrentName))
            {
                Lang.Apply(name);
                Palette.SaveLanguage(name);
                ApplyLanguage();   // 带配方的状态文本(如目录汇总)会按新语言重新生成
                // 状态栏显示的是别的内容(目录汇总等)时不抢它的位置;显示的是上一次的切换通知或启动提示时,换成这一次的语言。
                if (!_status.HasRecipe || ReferenceEquals(_status.Recipe, _languageNotice) || ReferenceEquals(_status.Recipe, _startupNotice))
                {
                    _languageNotice = () => Lang.Get("md_lang_switched", name);
                    _status.Localized(_languageNotice);
                }
            }
        };
        Tip(languageBox, "md_tip_lang");
        _toolbar.Children.Add(languageBox);
        Tip(_sessionBox, "session_box_tip"); Tip(_recentBox, "recent_box_tip");
        _toolbar.Children.Add(_sessionBox); _toolbar.Children.Add(_recentBox); RefreshSessions();
        // 第一项是标题(见 RefreshSessions):选中的序号减一才是条目;载入后框回到标题。
        _sessionBox.SelectionChanged += (_, _) => Run(async () => { if (_syncingSessions) return; if (_sessionBox.SelectedIndex > 0 && _sessions.Named.Count >= _sessionBox.SelectedIndex) await LoadSession(_sessions.Named[_sessionBox.SelectedIndex - 1]); _sessionBox.SelectedIndex = 0; });
        _recentBox.SelectionChanged += (_, _) => Run(async () => { if (_syncingSessions) return; if (_recentBox.SelectedIndex > 0 && _sessions.Recent.Count >= _recentBox.SelectedIndex) await LoadSession(_sessions.Recent[_recentBox.SelectedIndex - 1]); _recentBox.SelectedIndex = 0; });
        _toolbar.Children.Add(LButton("⇄", "md_tip_swap", () => Run(async () => { if (!await Leave()) return; _session.Clear(); RefreshEditors(); (_leftPath.Text, _rightPath.Text) = (_rightPath.Text, _leftPath.Text); await Compare(); })));
        _toolbar.Children.Add(_algorithm);
        Tip(_ignoreEol, "md_tip_eol"); Tip(_ignoreWhitespace, "md_tip_ws"); Tip(_ignoreCase, "md_tip_case");
        _toolbar.Children.Add(_ignoreEol); _toolbar.Children.Add(_ignoreWhitespace); _toolbar.Children.Add(_ignoreCase);
        _ignoreEol.IsCheckedChanged += (_, _) => ApplyOptions(); _ignoreWhitespace.IsCheckedChanged += (_, _) => ApplyOptions(); _ignoreCase.IsCheckedChanged += (_, _) => ApplyOptions();
        _toolbar.Children.Add(LButton("↑", "md_tip_prev", () => Navigate(-1)));
        _toolbar.Children.Add(LButton("↓", "md_tip_next", () => Navigate(1)));
        _toolbar.Children.Add(LButton("→", "md_tip_merge_l2r", () => Merge(MergeDirection.LeftToRight)));
        _toolbar.Children.Add(LButton("←", "md_tip_merge_r2l", () => Merge(MergeDirection.RightToLeft)));
        _toolbar.Children.Add(LButton("⇉", "md_tip_copyall_l2r", () => Run(() => CopyAllBlocks(true))));
        _toolbar.Children.Add(LButton("⇇", "md_tip_copyall_r2l", () => Run(() => CopyAllBlocks(false))));
        _toolbar.Children.Add(LButton("⇥", "md_tip_sync_l2r", () => Run(() => Sync(SyncMode.LeftToRight))));
        _toolbar.Children.Add(LButton("⇤", "md_tip_sync_r2l", () => Run(() => Sync(SyncMode.RightToLeft))));
        _toolbar.Children.Add(LButton("◷", "md_tip_history", () => Run(History)));
        RefreshFormatBox();
        Tip(_formatBox, "format_report");
        _toolbar.Children.Add(_formatBox);
        _toolbar.Children.Add(LButton("⤓", "export", () => Run(() => ExportReportAsync())));
        var paths = Columns(PathPanel(_leftPath), PathPanel(_rightPath));
        _diffTab.Header = Lang.Get("md_tab_diff");
        _diffScroll.Content = new Grid { Children = { _rows, _editorHost } };
        BuildFindBar();
        _diffHost.RowDefinitions = new RowDefinitions("Auto,*");
        _diffHost.Children.Add(_findBar);
        Grid.SetRow(_diffScroll, 1); _diffHost.Children.Add(_diffScroll);
        _diffTab.Content = _diffHost;
        _editorHost.Children.Add(_cellEditor); _cellEditor.IsVisible = false;
        _cellEditor.TextChanged += (_, _) => { if (_editingRow is { } row && !_editorLoading) CellEdited(row, _editingLeft, _cellEditor); };
        _cellEditor.KeyDown += (_, e) => { if (e.Key is Avalonia.Input.Key.Enter or Avalonia.Input.Key.Escape) { if (e.Key == Avalonia.Input.Key.Escape) _editorLoading = true; EndCellEdit(); e.Handled = true; } };
        _cellEditor.LostFocus += (_, _) => EndCellEdit();
        _rows.EditRequested += (row, left) =>
        {
            if (_awaitingAlignTarget) { CompleteAlign(row, left); return; }
            if (HexMode && !left) { _status.Text = Lang.Get("md_hex_edit_left_only"); return; }
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
                menu.Items.Add(lineCopy); menu.Items.Add(selectionCopy); menu.Items.Add(section); menu.Items.Add(goTo);
                menu.Items.Add(new Separator());
                // 文本转换(修剪空白/Tab/行尾等)由 IFileTypeProvider 插件提供 —— GenericFileTools 通配 "*" 覆盖全部扩展名,
                // 走下方 FileTypeMenus 的插件发现,宿主不再硬编码转换实现。
            }
            if (HexMode)
            {
                var gotoOffset = new MenuItem { Header = Lang.Get("md_menu_goto_offset") };
                gotoOffset.Click += (_, _) => Run(GoToOffsetAsync);
                var patch = new MenuItem { Header = Lang.Get("md_menu_export_patch") };
                patch.Click += (_, _) => Run(ExportHexPatchAsync);
                var saveHex = new MenuItem { Header = Lang.Get("md_menu_save_hex") + (_hexDirty ? " ●" : "") };
                saveHex.Click += (_, _) => Run(async () => { if (SaveHexEdit()) await Compare(); });   // 保存成功后在同一 Run 内重比较
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
        _editTab.Header = Lang.Get("md_tab_edit"); _editTab.Content = Columns(EditPanel(true), EditPanel(false));
        _folderTab.Header = Lang.Get("md_tab_folder");
        _tabs.ItemsSource = new[] { _diffTab, _editTab, _folderTab };
        InitializeFolderView();   // 目录页:筛选栏 + 行模板 + 右键菜单 + 双击打开(见 MainWindow.Folder.cs)
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(10), RowSpacing = 8 };
        foreach (var control in _toolbar.Children) control.Margin = new Thickness(2, 0, 2, 3);   // WrapPanel 没有 Spacing:用外边距留出间隔
        root.Children.Add(_toolbar); Grid.SetRow(paths, 1); root.Children.Add(paths); Grid.SetRow(_tabs, 2); root.Children.Add(_tabs); Grid.SetRow(_status.Block, 3); root.Children.Add(_status.Block); Content = root;
        _leftEditor.TextChanged += (_, _) => Edited(true); _rightEditor.TextChanged += (_, _) => Edited(false);
        // A box's TextChanged comes after its text changed, on a later pass. Until then the box is ahead of its buffer,
        // and SyncEditors must leave it alone: it would write the buffer's older text over what was just typed.
        foreach (var (box, left) in new[] { (_leftEditor, true), (_rightEditor, false) })
        {
            void Typing(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _typingIn |= left ? EditTyping.Left : EditTyping.Right;
            // On the way down: the box handles both events itself (typing, the paste key), and a handler that only sees
            // unhandled events would never learn of them.
            box.AddHandler(Avalonia.Input.InputElement.TextInputEvent, Typing, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            box.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, Typing, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        }
        _debounce.Tick += (_, _) => { _debounce.Stop(); try { _session.Recompare(); RefreshRows(); } catch (Exception ex) { _status.Text = Lang.Text(ex.Message); } };
        _algorithm.SelectionChanged += (_, _) => { _session.Algorithm = _algorithm.SelectedItem as string ?? FirstAlgorithmId(); _rows.Rows = []; _debounce.Start(); };
        Closing += async (_, e) =>
        {
            if (_allowClose) return;
            if (_busy) { e.Cancel = true; _status.Text = Lang.Get("md_close_wait"); return; }
            if (!_session.Dirty) return;
            e.Cancel = true; if (_closePending) return; _closePending = true;
            try { if (await Leave()) { _allowClose = true; Close(); } }
            catch (Exception ex) { _status.Text = Lang.Get("md_close_save_failed", Lang.Text(ex.Message)); }
            finally { _closePending = false; }
        };
        Closed += (_, _) => { _debounce.Stop(); if (_ownsPlugins) _plugins.Dispose(); System.Threading.Interlocked.Decrement(ref _windowCount); };
        if (_plugins.Diagnostics.Count > 0) { _status.Text = string.Join("\n", _plugins.Diagnostics.Select(Lang.Text)); _toolbar.IsEnabled = false; }
        else
        {
            // 声明式文件(语法文法/语言包/主题)加载失败要报告而不是静默跳过 —— 状态栏计数,对话框逐文件给出行列与原因。
            var issues = _plugins.GetAll<ISyntaxGrammar>().SelectMany(g => g.LoadDiagnostics)
                .Concat(Lang.LoadDiagnostics).Concat(Palette.Diagnostics).ToList();
            if (issues.Count > 0)
            {
                _status.Localized(() => Lang.Get("status_declarative_failed", issues.Count));
                Opened += (_, _) => Run(async () => await Ask(Lang.Get("dlg_declarative_title"),
                    Lang.Get("dlg_declarative_body", string.Join("\n  ", issues.Select(Lang.Text))), [Lang.Get("md_folder_close")]));
            }
            else { _startupNotice = () => Lang.Get("md_plugins_loaded_hint", _plugins.GetAll<IPlugin>().Count); _status.Localized(_startupNotice); }
        }
        this.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.G && (e.KeyModifiers & (Avalonia.Input.KeyModifiers.Meta | Avalonia.Input.KeyModifiers.Control)) != 0)
            { e.Handled = true; Run(GoToLineAsync); }
            var command = Avalonia.Input.KeyModifiers.Meta | Avalonia.Input.KeyModifiers.Control;
            if (e.Key == Avalonia.Input.Key.F && (e.KeyModifiers & command) != 0 && !HexMode)
            { e.Handled = true; ShowFind(); }
            else if (e.Key == Avalonia.Input.Key.F3 && _findBar.IsVisible)
            { e.Handled = true; FindNext(e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift)); }
            else if (e.Key == Avalonia.Input.Key.Escape && _findBar.IsVisible)
            { e.Handled = true; CloseFind(); }   // the find box handles its own Esc; this one is for when the focus is elsewhere
            if (e.Key == Avalonia.Input.Key.Escape && _awaitingAlignTarget)
            { _awaitingAlignTarget = false; _status.Text = Lang.Get("status_align_cancelled"); e.Handled = true; }
            var shift = Avalonia.Input.KeyModifiers.Shift;
            // Cmd+S saves the side that has the focus (a text box of the edit tab), or both sides when neither does;
            // Shift+Cmd+S saves both. In the hex view it writes the edited bytes to disk.
            if (e.Key == Avalonia.Input.Key.S && (e.KeyModifiers & command) != 0)
            {
                e.Handled = true;
                if (HexMode) Run(async () => { if (SaveHexEdit()) await Compare(); });
                else
                {
                    var focused = FocusManager?.GetFocusedElement() as Control;
                    Run(async () =>
                    {
                        bool side = ReferenceEquals(focused, _leftEditor) || ReferenceEquals(focused, _rightEditor);
                        if (side && !e.KeyModifiers.HasFlag(shift)) await _session.SaveAsync(ReferenceEquals(focused, _leftEditor));
                        else { await _session.SaveAsync(true); await _session.SaveAsync(false); }
                        RefreshDirty();
                    });
                }
            }
            else if (e.Key == Avalonia.Input.Key.F5)
            { e.Handled = true; Run(Compare); }
            else if (e.Key == Avalonia.Input.Key.Z && (e.KeyModifiers & command) != 0 && _tabs.SelectedIndex == 0 && !HexMode)
            {
                // The edit tab's text boxes undo their own typing. This is for the diff tab, where a change went into
                // the comparison itself: it takes the last step back on each side (Shift+Cmd+Z puts it again).
                e.Handled = true;
                Run(() => { _session.History(true, e.KeyModifiers.HasFlag(shift)); _session.History(false, e.KeyModifiers.HasFlag(shift)); RefreshEditors(); return Task.CompletedTask; });
            }
        });

        // Files dropped on the window: two fill the left and the right path, one fills the half it was dropped on
        // (as the WPF GUI does). The paths are filled in, not compared: Enter or the compare button does that.
        Avalonia.Input.DragDrop.SetAllowDrop(this, true);
        AddHandler(Avalonia.Input.DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = Avalonia.Input.DataTransferExtensions.Contains(e.DataTransfer, Avalonia.Input.DataFormat.File) ? Avalonia.Input.DragDropEffects.Copy : Avalonia.Input.DragDropEffects.None;
        });
        AddHandler(Avalonia.Input.DragDrop.DropEvent, (_, e) =>
        {
            e.Handled = true;
            if (_busy || !Avalonia.Input.DataTransferExtensions.Contains(e.DataTransfer, Avalonia.Input.DataFormat.File)) return;
            string[] dropped = Avalonia.Input.DataTransferExtensions.TryGetFiles(e.DataTransfer)?.Select(Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath).OfType<string>().ToArray() ?? [];
            if (dropped.Length == 0) return;
            if (dropped.Length > 1) { _leftPath.Text = dropped[0]; _rightPath.Text = dropped[1]; }
            else if (e.GetPosition(this).X < Bounds.Width / 2) _leftPath.Text = dropped[0];
            else _rightPath.Text = dropped[0];
        });
        if (Palette.IsDark) RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        if (Palette.SavedLanguage is { } saved && Lang.Available.Contains(saved)) Lang.Apply(saved);
        _languageBox.SelectedItem = Lang.CurrentName;   // 恢复保存语言后回同步下拉框显示(此前恢复 English 仍显示"中文")
        ApplyLanguage();
        if (args.Contains("--auto-compare")) Opened += (_, _) => Run(Compare);
    }
    /// <summary>默认比较算法:取目录中第一个算法插件("myers" 仅为空目录时的兜底,不再硬编码为指定算法)。</summary>
    private string FirstAlgorithmId() => _plugins.GetAll<IDiffAlgorithm>().FirstOrDefault()?.Id ?? "myers";
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
            var item = new MenuItem { Header = batch > 1 ? Lang.Get("menu_split_many", Lang.Text(chosen.DisplayName), batch) : Lang.Get("edit_desc_split", Lang.Text(chosen.DisplayName)), Tag = Math.Max(1, batch) };
            item.Click += (_, _) => { if (item.Tag is int n && n > 1) ApplySplitBatch(left, chosen); else ApplySplit(row, left, chosen); };
            menu.Items.Add(item);
        }
        return menu;
    }
    private async Task GoToLineAsync()
    {
        if (_rows.RowCount == 0) { _status.Text = Lang.Get("status_open_file_first"); return; }
        string? input = await Prompt(Lang.Get("dlg_goto_line_title"), Lang.Get("md_goto_line_prompt", Lang.Get(_lastFocusLeft ? "side_left" : "side_right")), "1");
        if (!int.TryParse(input, out int line) || line < 1) return;
        bool left = _lastFocusLeft;
        for (int i = 0; i < _rows.RowCount; i++)
            if ((left ? _rows.Rows[i].Left : _rows.Rows[i].Right)?.LineNumber == line)
            {
                _rows.SelectRows([_rows.Rows[i]]); _rows.EnsureVisible(i); _rows.Focus();
                return;
            }
        _status.Text = Lang.Get("md_goto_line_missing", Lang.Get(left ? "side_left" : "side_right"), line);
    }
    /// <summary>把一行(或文本选区覆盖的行)复制到对侧:按对齐重建目标侧,幽灵位置落为插入,行尾沿用目标风格。</summary>
    public void CopyToSide(AlignedRow? row = null, bool? fromLeft = null)
    {
        if (_session.Left is null || _session.Right is null) { _status.Text = Lang.Get("status_open_file_first"); return; }
        var range = _rows.TextSelectionRange;
        int fromRow, toRow;
        if (range is { } r) { fromRow = r.FromRow; toRow = r.ToRow; }
        else
        {
            row ??= _rows.SelectedRows.FirstOrDefault();
            if (row is null) { _status.Text = Lang.Get("status_select_copy_rows"); return; }
            int index = _rows.IndexOfRow(row);
            if (index < 0) return;
            fromRow = toRow = index;
        }
        bool sourceLeft = fromLeft ?? range?.Left ?? _lastFocusLeft;
        bool usesCrLf = (sourceLeft ? _session.Right : _session.Left).Text.Contains("\r\n");
        var target = new List<string>();
        int copied = 0;
        var opens = new List<int>();   // 选中的行里对侧原本没有格子的:复制过去的文字就落在那一格(行跟着文字走时按这个落)
        for (int i = 0; i < _rows.RowCount; i++)
        {
            var current = _rows.Rows[i];
            bool inSelection = i >= fromRow && i <= toRow;
            var sourceCell = sourceLeft ? current.Left : current.Right;
            var targetCell = sourceLeft ? current.Right : current.Left;
            if (inSelection && sourceCell is not null)
            {
                target.Add(sourceCell.Text + (usesCrLf ? "\r" : ""));   // 复制行沿用目标侧整体行尾风格
                copied++;
                if (targetCell is null) opens.Add(i);
            }
            else if (targetCell is not null)
                // 逐行保留原行尾(单元格 Ending:CRLF/CR→\r,LF/EOF→无),避免整侧重建丢掉 CRLF
                target.Add(targetCell.Text + (targetCell.Ending is "CRLF" or "CR" ? "\r" : ""));
            // 对侧为幽灵且不在选区:目标本就没有这一行,保持没有。
        }
        string previous = (sourceLeft ? _session.Right : _session.Left).Text;
        string joined = string.Join("\n", target);
        if (previous.EndsWith("\n") && !joined.EndsWith("\n"))
            joined += joined.EndsWith("\r") ? "\n" : previous.EndsWith("\r\n") ? "\r\n" : "\n";   // 保留末行换行,避免重复 \r
        EditSide(!sourceLeft, joined, opens);
        _debounce.Stop(); RefreshDirty();
        if ((bool)_autoRealign.IsChecked!) Realign();
        else _status.Text = Lang.Get("md_copied_rows_realign", copied, Lang.Get(sourceLeft ? "side_right" : "side_left"));
    }
    // ─── Align With / Isolate(引擎已支持:AlignmentHint + isolatedLines)───
    private void StartAlignWith(AlignedRow row, bool left)
    {
        var cell = left ? row.Left : row.Right;
        if (cell is null) { _status.Text = Lang.Get("md_align_no_rows"); return; }
        _alignSourceLine = cell.LineNumber;
        _alignSourceIsLeft = left;
        _awaitingAlignTarget = true;
        _status.Text = Lang.Get("md_align_pick_target", Lang.Get(left ? "side_left" : "side_right"), cell.LineNumber);
    }
    private void CompleteAlign(AlignedRow target, bool targetLeft)
    {
        _awaitingAlignTarget = false;
        if (targetLeft == _alignSourceIsLeft) { _status.Text = Lang.Get("md_align_other_side"); return; }
        var targetCell = targetLeft ? target.Left : target.Right;
        if (targetCell is null || _alignSourceLine < 1) { _status.Text = Lang.Get("md_align_target_invalid"); return; }
        int leftLine = targetLeft ? targetCell.LineNumber : _alignSourceLine;
        int rightLine = targetLeft ? _alignSourceLine : targetCell.LineNumber;
        _session.AlignmentHints = [.. _session.AlignmentHints, new(leftLine, rightLine)];
        _session.Recompare(); RefreshRows();
        _status.Text = Lang.Get("md_align_added", leftLine, rightLine, _session.AlignmentHints.Count);
    }
    private void IsolateSelection()
    {
        var rows = _rows.SelectedRows;
        if (rows.Count == 0) { _status.Text = Lang.Get("status_select_isolate"); return; }
        var leftLines = new List<int>(_session.IsolatedLeftLines);
        var rightLines = new List<int>(_session.IsolatedRightLines);
        foreach (var row in rows)
        {
            if (row.Left is { } l && !leftLines.Contains(l.LineNumber)) leftLines.Add(l.LineNumber);
            if (row.Right is { } r && !rightLines.Contains(r.LineNumber)) rightLines.Add(r.LineNumber);
        }
        _session.IsolatedLeftLines = leftLines; _session.IsolatedRightLines = rightLines;
        _session.Recompare(); RefreshRows();
        _status.Text = Lang.Get("md_isolated", rows.Count, leftLines.Count, rightLines.Count);
    }
    /// <summary>测试/程序化入口:添加手动对齐提示并立即重排。</summary>
    public void AddAlignmentHint(int leftLine, int rightLine)
    {
        _session.AlignmentHints = [.. _session.AlignmentHints, new(leftLine, rightLine)];
        _session.Recompare(); RefreshRows();
    }
    /// <summary>按点击侧的扩展名发现文件类型插件:专属提供者优先,无专属时回退通配 "*" 的通用工具。</summary>
    private List<IFileTypeProvider> ProvidersFor(bool left)
    {
        string extension = Path.GetExtension((left ? _session.Left : _session.Right)?.Path ?? "");
        var providers = _plugins.GetAll<IFileTypeProvider>()
            .Where(p => p.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)).ToList();
        if (providers.Count == 0)   // 无专属提供者 → 通配 "*" 的通用文件工具
            providers = _plugins.GetAll<IFileTypeProvider>().Where(p => p.Extensions.Contains("*", StringComparer.Ordinal)).ToList();
        return providers;
    }
    /// <summary>测试/程序化入口:列出指定侧可用的文件类型操作标签。</summary>
    public IReadOnlyList<string> FileTypeActionLabels(bool left) =>
        ProvidersFor(left).SelectMany(p => p.EditActions.Select(a => p.LanguageName + ":" + a.Label)).ToList();
    /// <summary>测试/程序化入口:执行指定侧第 index 个文件类型操作(按 FileTypeActionLabels 顺序)。</summary>
    public void RunFileTypeAction(bool left, int index)
    {
        var actions = ProvidersFor(left).SelectMany(p => p.EditActions).ToList();
        if (index < 0 || index >= actions.Count) return;
        var buffer = left ? _session.Left : _session.Right;
        if (buffer is null) return;
        string result = actions[index].Transform(new(buffer, 1, 1));
        if (string.IsNullOrEmpty(result) || result == buffer.Text) { _status.Text = Lang.Get("md_no_change"); return; }
        EditSide(left, result);
        _debounce.Stop(); RefreshDirty();
        if ((bool)_autoRealign.IsChecked!) Realign();
    }
    private void ClearAlignmentHints()
    {
        _session.AlignmentHints = []; _session.IsolatedLeftLines = []; _session.IsolatedRightLines = [];
        _session.Recompare(); RefreshRows();
        _status.Text = Lang.Get("status_align_cleared");
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
            var submenu = new MenuItem { Header = Lang.Get("md_filetype_menu", Lang.Text(capturedProvider.LanguageName), Lang.Get(left ? "side_left" : "side_right")) };
            foreach (var (label, _, transform) in capturedProvider.EditActions)
            {
                var action = transform;
                var item = new MenuItem { Header = Lang.Text(label) };
                item.Click += (_, _) => Run(async () =>
                {
                    var buffer = left ? _session.Left : _session.Right;
                    if (buffer is null) return;
                    try
                    {
                        string result = action(new(buffer, (left ? _rows.SelectedRows.FirstOrDefault()?.Left : _rows.SelectedRows.FirstOrDefault()?.Right)?.LineNumber ?? 1, 1));
                        if (string.IsNullOrEmpty(result) || result == buffer.Text) { _status.Text = Lang.Get("md_no_change"); return; }
                        EditSide(left, result);
                        _debounce.Stop(); RefreshDirty();
                        if ((bool)_autoRealign.IsChecked!) Realign();
                        else _status.Text = Lang.Get("md_tool_done_realign", Lang.Text(label));
                    }
                    catch (Exception ex) { _status.Text = Lang.Get("md_tool_failed", Lang.Text(label), Lang.Text(ex.Message)); }
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
        string? input = await Prompt(Lang.Get("md_goto_offset_title"), Lang.Get("md_goto_offset_prompt"), "0");
        if (string.IsNullOrWhiteSpace(input)) return;
        string trimmed = input.Trim();
        // 仅 0x 前缀按十六进制解析;纯数字按十进制(此前十进制输入被先按 hex 解析,回退分支永不命中)。
        bool hex = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!long.TryParse(hex ? trimmed[2..] : trimmed,
            hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer,
            CultureInfo.InvariantCulture, out long offset))
        { _status.Text = Lang.Get("status_bad_offset"); return; }
        long max = Math.Max(_hexLeft!.Length, _hexRight!.Length);
        if (offset < 0 || offset >= max) { _status.Text = Lang.Get("md_offset_range", max); return; }
        int index = (int)(offset / 16);
        _rows.SelectRows([_rows.Rows[index]]); _rows.EnsureVisible(index);
        _status.Text = Lang.Get("md_offset_found", offset, index + 1);
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
        _status.Text = Lang.Get("md_patch_exported", segments);
    }
    /// <summary>把十六进制编辑写盘并返回是否成功;调用方自行决定是否重新比较。不再内部 Run(Compare) ——
    /// 本方法常在菜单的 Run 内被调用,嵌套 Run 会被 _busy 短路成静默 no-op(保存后视图停在旧差异)。
    /// 视图只装了前 1MB:文件更大时拼回未加载的尾部再原子写,绝不截断原文件(审查 K2)。</summary>
    public bool SaveHexEdit()
    {
        if (!HexMode) return false;
        if (!_hexDirty) { _status.Text = Lang.Get("status_nothing_to_save"); return false; }
        try
        {
            var edited = _hexLeft!;
            long originalLength = new FileInfo(_hexLeftPath).Length;
            if (originalLength == edited.Length)
                AtomicFile.WriteAllBytes(_hexLeftPath, edited);
            else
            {
                byte[] full;
                using (var original = File.OpenRead(_hexLeftPath))
                {
                    full = new byte[originalLength];
                    original.ReadAtLeast(full, (int)Math.Min(originalLength, edited.Length));
                }
                Array.Copy(edited, full, edited.Length);   // 编辑只发生在已加载前缀内,尾部保持原样
                AtomicFile.WriteAllBytes(_hexLeftPath, full);   // 读句柄必须先关:File.Replace 换不掉本进程打开的文件
            }
            _hexDirty = false;
            _status.Text = Lang.Get("status_hex_saved");
            return true;
        }
        catch (Exception ex) { _status.Text = Lang.Get("md_save_failed", Lang.Text(ex.Message)); return false; }
    }
    private void FormatSide()
    {
        if (_loading || _session.Left is null || _session.Right is null) return;
        bool left = _lastFocusLeft;
        var buffer = left ? _session.Left : _session.Right;
        var formatter = _plugins.GetAll<ICodeFormatter>().FirstOrDefault(f => f.CanFormat(buffer.Path));
        if (formatter is null) { _status.Text = Lang.Get("status_no_formatter", Path.GetFileName(buffer.Path), Lang.Get("status_formatter_list", string.Join(", ", _plugins.GetAll<ICodeFormatter>().Select(f => Lang.Text(f.DisplayName))))); return; }
        try
        {
            var bare = buffer.Text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            var formatted = formatter.Format(bare);
            EditSide(left, LineText.Rebuild(formatted, buffer.Text));   // 行尾风格/结尾换行沿原文 —— 走 Core 助手,不再手写 join(LFCR 事故源;与上游 1caf78c 的内联修正语义一致)
            _debounce.Stop();
            RefreshDirty();
            if ((bool)_autoRealign.IsChecked!) Realign();
            else _status.Text = Lang.Get("md_format_done_realign", Lang.Text(formatter.DisplayName), Lang.Get(left ? "side_left" : "side_right"));
        }
        catch (Exception ex) { _status.Text = Lang.Get("status_format_failed", Lang.Text(ex.Message)); }
    }
    private void Realign()
    {
        if (_session.Left is null || _session.Right is null) { Run(Compare); return; }
        try { _session.Recompare(); RefreshRows(); }
        catch (Exception ex) { _status.Text = Lang.Get("md_realign_failed", Lang.Text(ex.Message)); }
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
            var parts = splitter.Split(raw.TrimEnd('\r'));
            if (parts.Count < 2) continue;
            lines[cell.LineNumber - 1] = LineText.ReplaceRawLine(raw, parts);   // Core 助手:行尾风格沿用原行(上游同修)
            applied++;
        }
        if (applied == 0) return;
        EditSide(left, string.Join("\n", lines));
        _debounce.Stop();
        RefreshDirty();
        if ((bool)_autoRealign.IsChecked!) Realign();
        else _status.Text = Lang.Get("md_split_done_realign", Lang.Text(splitter.DisplayName), applied);
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
            var parts = splitter.Split(raw.TrimEnd('\r'));
            if (parts.Count < 2) return;
            lines[index] = LineText.ReplaceRawLine(raw, parts);   // 行尾风格(CRLF)沿用原行 —— Core 助手(上游同修)
            EditSide(left, string.Join("\n", lines));
            _debounce.Stop();   // 手动模式:点 ⟳ 重对齐;⚡ 开启时立即重算
            RefreshDirty();
            if ((bool)_autoRealign.IsChecked!) Realign();
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
    /// <summary>测试/交互入口:在指定行指定侧弹出行内编辑器(与视图点击同一通路)。</summary>
    public void BeginEditRow(int index, bool left)
    {
        if (HexMode && !left) { _status.Text = Lang.Get("md_hex_edit_left_only"); return; }
        if (index < 0 || index >= _rows.RowCount) return;
        var row = _rows.Rows[index];
        var cell = left ? row.Left : row.Right;
        if (cell is null) return;
        if (_editingRow is not null) EndCellEdit();   // 换单元格前提交上一个
        _editingRow = row; _editingLeft = left; _lastFocusLeft = left;
        _editorLoading = true; _cellEditor.Text = cell.Text; _editorLoading = false;
        _cellCommitted = cell.Text;
        var rect = _rows.CellRect(index, left);
        _cellEditor.Width = rect.Width; _cellEditor.Height = AlignedRowsView.RowHeight;
        Canvas.SetLeft(_cellEditor, rect.X); Canvas.SetTop(_cellEditor, rect.Y);
        _cellEditor.IsVisible = true;
        _cellEditor.Focus(); _cellEditor.CaretIndex = _cellEditor.Text.Length;
        _rows.SelectRows([row]);
    }
    /// <summary>差异页上对缓冲区的每一次修改都从这里进:会话把它记成一次编辑,屏幕上的行跟着文字走(RowFollow)。
    /// 左右的对齐不在这里重算 —— 那是 ⟳ 或 ⚡ 的事。此前各处直接调会话,编辑之后屏幕上的行就留在了旧文字上。</summary>
    private void EditSide(bool left, string text, IReadOnlyList<int>? fillRows = null)
    {
        var buffer = left ? _session.Left : _session.Right;
        if (buffer is null || buffer.Text == text) return;
        string before = buffer.Text;
        var shown = _rows.Rows;
        int editing = _editingRow is { } open ? _rows.IndexOfRow(open) : -1;
        _session.Edit(left, text);
        if (HexMode || shown.Count == 0) return;   // 十六进制行不是文字的行;行是空的也没有什么可跟(编辑页打字时如此)
        var followed = RowFollow.Follow(shown, left, before, buffer.Text, fillRows);
        if (followed is null) { Realign(); return; }   // 行集合和这份文字对不上:重新比较,不猜
        _rows.FollowRows(followed);
        UpdateFindCount();   // 匹配是按屏幕上的行文字算的:行变了,计数跟着变
        _rowsAligned = false;
        _editingRow = editing >= 0 && followed.OldToNew[editing] >= 0 ? _rows.Rows[followed.OldToNew[editing]] : null;
        if (_editingRow is null) _cellEditor.IsVisible = false;
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
            string ending = lines[index].EndsWith('\r') ? "\r" : "";   // 沿用这一格原有的行尾风格(CRLF)
            string[] written = CellLines(_cellCommitted), typed = CellLines(editor.Text ?? "");
            if (index + written.Length > lines.Length) return;
            // 编辑框的文字换掉的是它上一次写进去的那几行 —— 粘贴进来的可以有好几行,而同一次改动会报到两次。
            // 那几行应当还在原处;不在(别处改过)就只认这一行。
            bool mine = true;
            for (int k = 0; k < written.Length && mine; k++) mine = lines[index + k] == written[k] + ending;
            if (!mine) written = [lines[index].EndsWith('\r') ? lines[index][..^1] : lines[index]];
            if (typed.Length == written.Length && typed.SequenceEqual(written)) return;
            var updated = new string[lines.Length - written.Length + typed.Length];
            Array.Copy(lines, updated, index);
            for (int k = 0; k < typed.Length; k++) updated[index + k] = typed[k] + ending;   // 段里每一行都用这一格的行尾
            Array.Copy(lines, index + written.Length, updated, index + typed.Length, lines.Length - index - written.Length);
            _cellCommitted = editor.Text ?? "";
            EditSide(left, string.Join("\n", updated));
            _debounce.Stop();   // 手动重对齐:编辑时不自动重排,点 ▶ 按当前算法重新对齐
            RefreshDirty();
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
    /// <summary>编辑框里的文字按行拆开,行尾统一成 \n(文本框会把粘贴进来的行尾留成原样)。</summary>
    private static string[] CellLines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

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
        buttons.Children.Add(LButton("▤", "md_tip_pick_file", () => Run(async () => { var items = await StorageProvider.OpenFilePickerAsync(new() { AllowMultiple = false }); if (items.Count > 0) path.Text = items[0].TryGetLocalPath(); })));
        buttons.Children.Add(LButton("▣", "md_tip_pick_dir", () => Run(async () => { var items = await StorageProvider.OpenFolderPickerAsync(new() { AllowMultiple = false }); if (items.Count > 0) path.Text = items[0].TryGetLocalPath(); })));
        Tip(path, "path_tip");
        path.KeyDown += (_, e) => { if (e.Key != Avalonia.Input.Key.Enter) return; e.Handled = true; CommitPathBars(); };
        DockPanel.SetDock(buttons, Dock.Right); panel.Children.Add(buttons); panel.Children.Add(path); return panel;
    }
    private Control EditPanel(bool left)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        bar.Children.Add(left ? _leftSideLabel : _rightSideLabel);
        bar.Children.Add(LButton("▣", "md_tip_save_side", () => Run(async () => { await _session.SaveAsync(left); RefreshDirty(); })));
        var undo = Button("↶", "", () => { try { _session.History(left, false); RefreshEditors(); } catch (Exception ex) { _status.Text = ex.Message; } });
        Tip(undo, "md_undo_side"); bar.Children.Add(undo);
        var redo = Button("↷", "", () => { try { _session.History(left, true); RefreshEditors(); } catch (Exception ex) { _status.Text = ex.Message; } });
        Tip(redo, "md_redo_side"); bar.Children.Add(redo);
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") }; grid.Children.Add(bar); var editor = left ? _leftEditor : _rightEditor; Grid.SetRow(editor, 1); grid.Children.Add(editor); return grid;
    }
    private async void Run(Func<Task> operation)
    {
        if (_busy) return; _busy = true; _toolbar.IsEnabled = false; _tabs.IsEnabled = false; _leftPath.IsEnabled = _rightPath.IsEnabled = false;
        try { await operation(); } catch (Exception ex) { _status.Text = Lang.Get("md_operation_failed", Lang.Text(ex.Message)); }
        finally { _busy = false; _toolbar.IsEnabled = true; _tabs.IsEnabled = true; _leftPath.IsEnabled = _rightPath.IsEnabled = true; }
    }
    private void ApplyOptions()
    {
        _session.Options = BuildOptions();
        if (_session.Left is null) return;
        _rows.Rows = []; _debounce.Stop(); _debounce.Start();
    }
    private ComparisonOptions BuildOptions() => new((bool)_ignoreCase.IsChecked!, (bool)_ignoreWhitespace.IsChecked!, (bool)_ignoreEol.IsChecked!, UnimportantPatterns: _unimportant);
    /// <summary>两个下拉框的第一项是标题:它不对应任何条目 —— 没有会话时它写明"暂无",选过一项后框回到它。
    /// 此前列表为空(以及每次选完之后)这两个框是空白的,看上去像坏了。</summary>
    private bool _syncingSessions;
    private void RefreshSessions()
    {
        _syncingSessions = true;
        try
        {
            _sessionBox.ItemsSource = WithCaption("sessions", "md_session_empty", _sessions.Named.Select(s => $"📌 {s.Name} · {Path.GetFileName(s.Left)} ↔ {Path.GetFileName(s.Right)}"));
            _recentBox.ItemsSource = WithCaption("recent", "md_recent_empty", _sessions.Recent.Select(s => $"🕘 {Path.GetFileName(s.Left)} ↔ {Path.GetFileName(s.Right)}"));
            _sessionBox.SelectedIndex = 0; _recentBox.SelectedIndex = 0;
        }
        finally { _syncingSessions = false; }
    }
    private static string[] WithCaption(string captionKey, string emptyKey, IEnumerable<string> entries)
    {
        var list = entries.ToArray();
        return [Lang.Get(list.Length == 0 ? emptyKey : captionKey), .. list];
    }
    /// <summary>测试钩子:让窗口显示给定的一组会话与最近比较。</summary>
    internal void ShowSessions(SessionStore.SessionFile sessions) { _sessions = sessions; RefreshSessions(); }
    private async Task LoadSession(SavedSession session)
    {
        if (!await Leave()) return;
        _leftPath.Text = session.Left; _rightPath.Text = session.Right;
        _algorithm.SelectedItem = session.Algorithm ?? FirstAlgorithmId();
        _ignoreCase.IsChecked = session.IgnoreCase; _ignoreWhitespace.IsChecked = session.IgnoreWhitespace; _ignoreEol.IsChecked = session.IgnoreEol;
        _unimportant = session.UnimportantPatterns ?? [];
        _session.Algorithm = session.Algorithm ?? FirstAlgorithmId();
        _session.Options = BuildOptions();
        await Compare();
    }
    private async Task SaveSession()
    {
        if (string.IsNullOrWhiteSpace(_leftPath.Text) || string.IsNullOrWhiteSpace(_rightPath.Text)) { _status.Text = Lang.Get("status_session_need_paths"); return; }
        string? name = await Prompt(Lang.Get("dlg_session_title"), Lang.Get("dlg_session_name"), $"{Path.GetFileName(_leftPath.Text)} ↔ {Path.GetFileName(_rightPath.Text)}");
        if (string.IsNullOrWhiteSpace(name)) return;
        _sessions = _sessions.UpsertNamed(new(name, _leftPath.Text.Trim(), _rightPath.Text.Trim(), _session.Algorithm,
            (bool)_ignoreCase.IsChecked!, (bool)_ignoreWhitespace.IsChecked!, (bool)_ignoreEol.IsChecked!, true, DateTime.Now, _unimportant));
        SessionStore.Save(_sessions);
        RefreshSessions();
        _status.Text = Lang.Get("status_session_saved", name);
    }
    private async void EditRules()
    {
        var window = new Window { Title = Lang.Get("unimportant_rules"), Width = 600, Height = 340, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = true };
        var input = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Code,Menlo,DejaVu Sans Mono,monospace"), Text = string.Join("\n", _unimportant), Watermark = Lang.Get("unimportant_tip"), Margin = new Thickness(12), MinHeight = 180 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        var panel = new DockPanel { Margin = new Thickness(6) };
        // The buttons are told apart by a fixed value, not by their captions: the captions follow the UI language.
        foreach (var (caption, value) in new[] { (Lang.Get("md_apply"), "apply"), (Lang.Get("md_clear"), "clear"), (Lang.Get("dlg_cancel"), "cancel") })
        {
            string choice = value;
            buttons.Children.Add(Button(caption, caption, () => window.Close(choice == "cancel" ? null : choice == "clear" ? "" : input.Text ?? "")));
        }
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons); panel.Children.Add(input);
        window.Content = panel;
        string? result = await window.ShowDialog<string?>(this);
        if (result is null) return;
        var patterns = result.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        foreach (string pattern in patterns)
        {
            try { _ = new System.Text.RegularExpressions.Regex(pattern); }
            catch (ArgumentException ex) { _status.Text = Lang.Get("err_unimportant_rule", pattern, Lang.Text(ex.Message)); return; }
        }
        _unimportant = patterns;
        ToolTip.SetTip(_algorithm, AlgorithmTip());
        ApplyOptions();
        _status.Text = Lang.Get("md_rules_applied", _unimportant.Count);
    }
    private async Task<string?> Prompt(string title, string label, string defaultValue)
    {
        var window = new Window { Title = title, Width = 480, Height = 170, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var input = new TextBox { Text = defaultValue, Watermark = label, Margin = new Thickness(12) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        buttons.Children.Add(Button(Lang.Get("md_ok"), label, () => window.Close(input.Text)));
        buttons.Children.Add(Button(Lang.Get("dlg_cancel"), label, () => window.Close(null)));
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
            catch (Exception ex) { _status.Text = Lang.Get("md_realign_failed", Lang.Text(ex.Message)); return; }
            RefreshRows();
            return;
        }
        if (!await Leave()) return;
        if (string.IsNullOrWhiteSpace(_leftPath.Text) || string.IsNullOrWhiteSpace(_rightPath.Text)) throw new IOException(Lang.Get("status_fill_paths"));
        string left = Path.GetFullPath(_leftPath.Text.Trim()), right = Path.GetFullPath(_rightPath.Text.Trim());
        _hexLeft = null; _hexDirty = false;   // 上一轮十六进制状态对新比较无效(文本/目录/图片分支);二进制分支稍后重新设置
        _status.Localized(() => Lang.Get("status_comparing"));
        int run = ++_compareRun;
        bool folderPair = Directory.Exists(left) && Directory.Exists(right);
        // 目录比较先列出根目录,随后按增量生长(见 MainWindow.Folder.cs)。进度只入队不处理:单线程按报告顺序消费,
        // 每 120ms 合并应用一批;比较结束后队列作废,迟到的进度不可能覆盖最终状态。
        // 目录扫描期间目录页保持可用 —— 可以边扫边浏览、筛选(文本会话要等扫描结束才替换)。
        IProgress<ComparisonProgress> progress = new InlineProgress<ComparisonProgress>(EnqueueFolderProgress);
        ComparisonResult result;
        try
        {
            if (folderPair) { _tabs.IsEnabled = true; StartFolderScan(run); }
            result = await Task.Run(() => new ComparisonEngine(_plugins).CompareAsync(new(left, right, _session.Algorithm, _session.Options), progress: progress));
        }
        catch { StopFolderScan(); throw; }
        finally { _compareRun++; }   // this run is over (or failed): its late progress updates are dropped
        StopFolderScan();
        // 十六进制 / 图片 / 出错的文件对没有文本会话:导出报告时用的就是这一次比较的结果。
        _pair = folderPair ? null : result;
        if (folderPair)
        {
            // 扫描期间用户可能编辑了上一对文本:先问再丢弃。目录结果无论如何都落到列表里。
            bool keepSession = _session.Dirty && !await Leave();
            if (!keepSession) { _session.Clear(); RefreshEditors(); }
            ApplyFolderResult(result, switchToTab: !keepSession);
            RecordRecent(left, right);
            if (result.Warnings.Count > 0) _status.Text += Environment.NewLine + string.Join(Environment.NewLine, result.Warnings.Select(Lang.Text));
            return;
        }
        _session.Clear(); RefreshEditors();
        {
            _directory = null; _files.ItemsSource = null;
            var file = result.Files.Single();
            if (result.ModuleId == "pictures" && file.Status != FileStatus.Error)
            {
                ShowPicture(left, right, file);
                _tabs.SelectedIndex = 0;   // 差异页承载图片视图:此前不切 tab,目录页之后看似"没反应"
                _status.Text = Lang.Get("md_picture_status", Lang.Text(file.Note));
                RecordRecent(left, right);
                return;
            }
            if (file.IsBinary || file.Status == FileStatus.Error)
            {
                _rows.Rows = []; _tabs.SelectedIndex = 0;
                var (summary, verdict) = await BinarySummaryAsync(left, right);
                if (file.Status == FileStatus.Error) { _diffTab.Content = _binaryInfo; _binaryInfo.Text = summary; _status.Text = FolderStatusText(file.Status) + ": " + Lang.Text(file.Note); return; }
                ShowHex(left, right, file);
                const long hexCap = 1 << 20;
                string capNote = Math.Max(new FileInfo(left).Length, new FileInfo(right).Length) > hexCap ? Lang.Get("md_hex_cap_note") : "";
                _status.Text = Lang.Get("md_hex_view_status", capNote, verdict.Trim());
                RecordRecent(left, right);
                return;
            }
            await OpenFile(left, right);
        }
        RecordRecent(left, right);
        if (result.Warnings.Count > 0) _status.Text += Environment.NewLine + string.Join(Environment.NewLine, result.Warnings.Select(Lang.Text));
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
        _diffTab.Content = _diffHost; _hexLeft = null; _hexDirty = false; _pair = null;
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
        _diffTab.Content = _diffHost; _findBar.IsVisible = false;   // 十六进制行里查找文本没有意义
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
            byte[] leftBytes = _hexLeft ?? throw new InvalidOperationException(Lang.Get("md_hex_not_ready"));
            int rowBytes = (int)Math.Min(HexRowBytes, Math.Max(0, leftBytes.Length - offset));
            if (rowBytes == 0) return;
            var tokens = (editor.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length == 2 && Uri.IsHexDigit(t[0]) && Uri.IsHexDigit(t[1])).Take(rowBytes + 1).ToList();
            if (tokens.Count != rowBytes)
            {
                _status.Text = Lang.Get("md_hex_need_bytes", rowBytes);
                _editorLoading = true; editor.Text = DumpHex(leftBytes, offset); _editorLoading = false;
                return;
            }
            for (int i = 0; i < rowBytes; i++) leftBytes[offset + i] = Convert.ToByte(tokens[i], 16);
            _hexDirty = true;
            int editedRow = cell.LineNumber - 1;
            _rows.Rows = BuildHexRows(leftBytes, _hexRight ?? []);
            _rows.SelectRows([_rows.Rows[editedRow]]); _rows.EnsureVisible(editedRow);
            _status.Text = Lang.Get("md_hex_row_edited", editedRow + 1, rowBytes, Lang.Get("md_menu_save_hex"));
        }
        catch (Exception ex) { _status.Text = Lang.Get("md_hex_edit_failed", Lang.Text(ex.Message)); }
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
            var header = new TextBlock { Text = title + (overlayImage is null ? "" : Lang.Get("md_picture_overlay_note")), Foreground = Brush.Parse("#8793A4"), Margin = new Thickness(0, 4) };
            Control content;
            if (image is null)
                content = new TextBlock { Text = Lang.Get("md_picture_undecodable"), Foreground = Brush.Parse("#A52D22") };
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
        images.Children.Add(Pane(Load(left), Lang.Get("side_left"), null));
        var rightPane = Pane(Load(right), Lang.Get("side_right"), overlay);
        Grid.SetColumn(rightPane, 1);
        images.Children.Add(rightPane);
        panel.Children.Add(images);
        _diffTab.Content = panel; _findBar.IsVisible = false;
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
    /// <summary>What the hex view and an unreadable pair say about the two files, and the verdict on its own
    /// (the status line shows the verdict; the view shows the whole text).</summary>
    private static async Task<(string Text, string Verdict)> BinarySummaryAsync(string left, string right)
    {
        static async Task<(long Size, DateTime Time, string Hash)> Info(string path)
        {
            var file = new FileInfo(path);
            if (!file.Exists) return (0, default, Lang.Get("md_missing"));
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return (file.Length, file.LastWriteTime, Convert.ToHexString(await SHA256.HashDataAsync(stream))[..16]);
        }
        var (lSize, lTime, lHash) = await Info(left);
        var (rSize, rTime, rHash) = await Info(right);
        string verdict = Lang.Get(lSize == rSize && lHash == rHash ? "md_binary_identical" : "md_binary_different");
        return (Lang.Get("md_binary_summary", left, lSize, lTime, lHash, right, rSize, rTime, rHash) + "\n\n" + Lang.Get("md_binary_verdict", verdict), verdict);
    }
    private void Edited(bool left)
    {
        _typingIn &= left ? ~EditTyping.Left : ~EditTyping.Right;
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
    private void RefreshRows()
    {
        if (_editingRow is not null && (_session.Presentation?.Rows ?? []).All(r => !ReferenceEquals(r, _editingRow)))
            EndCellEdit();   // 行集合已换:覆盖层指向的行不存在了,收起
        _rows.Rows = _session.Presentation?.Rows ?? [];
        _rowsAligned = true;
        RefreshDirty();
    }
    private void BuildFindBar()
    {
        Tip(_findBox, "find");
        _findBox.TextChanged += (_, _) => ApplyFind();
        _findBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; FindNext(e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift)); }
            else if (e.Key == Avalonia.Input.Key.F3) { e.Handled = true; FindNext(e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift)); }
            else if (e.Key == Avalonia.Input.Key.Escape) { e.Handled = true; CloseFind(); }
        };
        Tip(_findCase, "md_find_case_tip");
        _findCase.IsCheckedChanged += (_, _) => ApplyFind();
        // Every control in its own column (they would all land in the first one and pile up).
        void Place(Control control, int column) { Grid.SetColumn(control, column); _findBar.Children.Add(control); }
        Place(new TextBlock { Text = "🔍", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }, 0);
        Place(_findBox, 1);
        Place(_findCount, 2);
        Place(_findCase, 3);
        Place(LButton("↑", "md_find_prev_tip", () => FindNext(true)), 4);
        Place(LButton("↓", "find_next_tip", () => FindNext(false)), 5);
        Place(LButton("✕", "md_find_close_tip", CloseFind), 6);
    }

    /// <summary>查找框里的文字。设置即打开查找条并重新找。</summary>
    public string FindText { get => _findBox.Text ?? ""; set { ShowFind(); _findBox.Text = value; ApplyFind(); } }
    public bool FindBarVisible => _findBar.IsVisible;
    /// <summary>当前匹配的序号(从 0 起,没有匹配为 -1)、匹配总数、当前匹配所在的行。</summary>
    public (int Index, int Count, int? Row) FindState => (_rows.FindIndex, _rows.FindMatchCount, _rows.FindMatch?.Row);
    public (int Row, bool Left, int Start, int Length)? FindMatch => _rows.FindMatch;
    public string FindCountText => _findCount.Text ?? "";
    public bool FindCaseSensitive { get => _findCase.IsChecked == true; set => _findCase.IsChecked = value; }
    public Avalonia.Controls.Primitives.ToggleButton FindCaseToggle => _findCase;
    public TextBox FindBox => _findBox;
    /// <summary>编辑页某一侧的整文编辑框(测试用它确认 ⌘S 保存的是聚焦的一侧)。</summary>
    public TextBox EditorBox(bool left) => left ? _leftEditor : _rightEditor;
    /// <summary>会话缓冲区本身(与编辑页的文本框相对):文本与是否有未保存的修改。测试用它区分"文本框里是什么"和"比较用的是什么"。</summary>
    public (string? Left, string? Right) SessionText => (_session.Left?.Text, _session.Right?.Text);
    public (bool Left, bool Right) SessionDirty => (_session.Left?.IsDirty == true, _session.Right?.IsDirty == true);

    /// <summary>打开查找条并把输入焦点放进去(差异页)。</summary>
    public void ShowFind()
    {
        if (_tabs.SelectedIndex != 0) _tabs.SelectedIndex = 0;
        _findBar.IsVisible = true;
        _findBox.Focus();
        _findBox.SelectAll();
    }

    public void CloseFind()
    {
        _findBox.Text = "";
        // Right here, not through the box's change event: that one is deferred a pass for a programmatic assignment, and until then
        // the view would still hold the matches (one more frame of highlights, and FindNext would still move).
        ApplyFind();
        _findBar.IsVisible = false;
        _rows.Focus();
    }

    /// <summary>下一处 / 上一处;计数文字跟着更新。没有匹配时返回 false。</summary>
    public bool FindNext(bool backwards = false)
    {
        bool found = _rows.FindNext(backwards);
        UpdateFindCount();
        return found;
    }

    private void ApplyFind()
    {
        _rows.SetFind(_findBox.Text ?? "", !FindCaseSensitive);
        UpdateFindCount();
    }

    private void UpdateFindCount() => _findCount.Text = _rows.FindMatchCount == 0
        ? ((_findBox.Text ?? "").Length > 0 && _findBar.IsVisible ? Lang.Get("md_find_none") : "")
        : $"{_rows.FindIndex + 1} / {_rows.FindMatchCount}";

    /// <summary>The edit tab's text boxes show the buffers. A change made anywhere else — an inline edit, a line copied
    /// to the other side, a tool of the context menu — has to reach them: while a box showed the old text, the next
    /// keystroke typed in it wrote the old text back over the change. A box is left alone while it already shows its
    /// buffer's text, so typing there keeps its caret. Every path that changes a buffer ends in RefreshDirty.</summary>
    private void SyncEditors()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            string left = _session.Left?.Text ?? "", right = _session.Right?.Text ?? "";
            // A box that took input waits for its own TextChanged: what is in it is newer than the buffer.
            if ((_typingIn & EditTyping.Left) == 0 && _leftEditor.Text != left) _leftEditor.Text = left;
            if ((_typingIn & EditTyping.Right) == 0 && _rightEditor.Text != right) _rightEditor.Text = right;
        }
        finally { _loading = false; }
    }

    // Between an edit and its realignment there is no presentation (in manual mode for the 250 ms of the debounce):
    // the count shown is the last one there was. Zero would be false, and the realign brings the true number.
    private int _knownHunks;
    // 屏幕上的行还是不是一次比较的结果。打字、复制行之后行跟着文字走(RowFollow),对齐没重算:块标记是旧的,状态栏照 WPF 的说法提示"未对齐"。
    private bool _rowsAligned = true;
    private void RefreshDirty()
    {
        SyncEditors();
        if (_session.Left is null) { _status.Text = Lang.Get("md_pick_path_hint"); return; }
        if (_session.Presentation is { } presentation) _knownHunks = presentation.Hunks.Count;
        // From a recipe: the status line says the same thing in whatever language is chosen later.
        _status.Localized(() => Lang.Get(_rowsAligned ? "md_edit_status" : "md_edit_status_unaligned", Lang.Get(_session.Dirty ? "edit_status_dirty" : "edit_status_saved"),
            _rowsAligned ? _knownHunks : Lang.Get("hunks_unaligned"), _session.Left.Path, _session.Right?.Path ?? ""));
    }
    private async Task CopyAllBlocks(bool toRight)
    {
        if (_session.Left is null || _session.Right is null) { _status.Text = Lang.Get("status_open_file_first"); return; }
        try
        {
            int copied = _session.MergeAll(toRight ? MergeDirection.LeftToRight : MergeDirection.RightToLeft);
            RefreshEditors();
            _status.Text = copied == 0 ? Lang.Get("md_no_hunks_to_copy") : Lang.Get("md_hunks_copied_unsaved", copied, Lang.Get(toRight ? "side_right" : "side_left"));
        }
        catch (Exception ex) { _status.Text = Lang.Get("md_copy_all_failed", Lang.Text(ex.Message)); }
    }
    private void Merge(MergeDirection direction)
    {
        if (_rows.SelectedRows.FirstOrDefault(r => r.HunkId >= 0) is not { } row) { _status.Text = Lang.Get("status_not_in_hunk"); return; }
        try
        {
            _debounce.Stop();
            // 屏幕上的行可能来自上一次编辑之前(打字、复制行之后对齐没重算):块序号说的是那时的排法。复制本身会重新对齐,
            // 所以先对齐,再按选中那一行的行号把它所在的块找回来。
            int hunk = row.HunkId;
            if (!_rowsAligned)
            {
                _session.Recompare(); RefreshRows();
                hunk = _rows.Rows.FirstOrDefault(r => r.HunkId >= 0 && ((row.Left is { } l && r.Left?.LineNumber == l.LineNumber) || (row.Right is { } rr && r.Right?.LineNumber == rr.LineNumber)))?.HunkId ?? -1;
                if (hunk < 0) { _status.Text = Lang.Get("status_not_in_hunk"); return; }
            }
            _session.Merge(hunk, direction); RefreshEditors();
        }
        catch (Exception ex) { _status.Text = ex.Message; }
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
        // Ask answers with the caption that was clicked, so that is what it is compared with.
        string save = Lang.Get("dlg_save"), discard = Lang.Get("md_discard");
        string choice = await Ask(Lang.Get("dlg_unsaved_title"), Lang.Get("md_unsaved_prompt"), [save, discard, Lang.Get("dlg_cancel")]);
        if (choice == save) { await _session.SaveAsync(true); await _session.SaveAsync(false); return true; }
        return choice == discard;
    }
    private async Task<string> Ask(string title, string text, string[] choices)
    {
        var window = new Window { Title = title, Width = 760, Height = 460, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new Thickness(16) }; var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (string choice in choices) buttons.Children.Add(Button(choice, choice, () => window.Close(choice)));
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons); panel.Children.Add(new ScrollViewer { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } }); window.Content = panel;
        return await window.ShowDialog<string?>(this) ?? Lang.Get("dlg_cancel");
    }
    private T Plugin<T>() where T : class, IPlugin => _plugins.GetAll<T>().First();
    private SyncSafetyContext Safety() => new(Path.Combine(AppPaths.DataDirectory, "SyncHistory"), Plugin<ISyncBackup>(), Plugin<ISyncJournal>());
    /// <summary>测试/程序化入口:按相对路径选中目录页文件(等价于用户在列表中多选)。</summary>
    public void SelectFolderFiles(params string[] relativePaths)
    {
        _files.SelectedItems?.Clear();
        foreach (var path in relativePaths)
        {
            var match = _folderRows.FirstOrDefault(f => f.RelativePath == path);
            if (match is not null) _files.SelectedItems?.Add(match);
        }
    }
    /// <summary>目录页批量复制所选文件到另一侧:缺源跳过、逐文件容错、完成后重比较并恢复选中。</summary>
    public async Task CopySelectedFolderFiles(bool fromLeft)
    {
        if (_directory is null) { _status.Text = Lang.Get("md_folder_need_compare"); return; }
        var selected = SelectedFolderFiles();   // 选中目录行 = 其下全部文件
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
        var restored = _folderRows.FirstOrDefault(f => f.RelativePath == reopen);
        if (restored is not null) _files.SelectedItem = restored;
        _status.Localized(() => Lang.Get("status_batch_copy_done", copied) + (failed > 0 ? Lang.Get("status_batch_copy_fail_suffix", failed) : ""));
    }
    private async Task Sync(SyncMode mode)
    {
        if (_directory is null) throw new IOException(Lang.Get("md_folder_need_compare"));
        if (!await Leave()) return;
        var targets = SyncTargetPaths();
        if (targets.Length == 0) { _status.Localized(() => Lang.Get("status_no_sync_files")); return; }
        var plan = await Plugin<ISyncPlanner>().PlanAsync(_directory.LeftPath, _directory.RightPath, targets, mode);
        // 预览里每个文件的复制方向都可以改;关掉预览(或按取消)时什么都不做。
        string scope = Lang.Get(SelectedFolderFiles().Length > 0 ? "sync_scope_selected" : "sync_scope_batch");
        var decisions = await new SyncPreviewWindow(plan, Plugin<ISyncPreview>().Describe(plan), scope).ShowDialog<IReadOnlyList<SyncDecision>?>(this);
        if (decisions is null) return;
        var result = await RunSyncAsync(plan, decisions);
        await Ask(Lang.Get("md_folder_sync_result_title"), string.Join("\n", DescribeSyncResult(result)), [Lang.Get("md_folder_close")]);
        await Compare();   // 列表已过时:重新比较目录以反映同步结果
    }
    /// <summary>按给定的决定执行同步(覆盖前备份并记录)并报告结果。决定来自预览窗口;测试直接给,
    /// 用来核对执行的是决定而不是插件的建议。</summary>
    public async Task<SyncResult> RunSyncAsync(SyncPlan plan, IReadOnlyList<SyncDecision> decisions)
    {
        var result = await Plugin<ISyncExecutor>().ExecuteAsync(plan, decisions, Safety());
        _session.Clear(); RefreshEditors();
        _status.Localized(() => Lang.Get("status_sync_done", result.Entries.Count(e => e.Outcome == SyncOutcome.Copied), result.Entries.Count(e => e.Outcome == SyncOutcome.Failed), result.Entries.Count(e => e.Outcome == SyncOutcome.Cancelled)) + " " + Lang.Get("md_folder_sync_refresh"));
        return result;
    }
    private async Task History()
    {
        var safety = Safety(); var listing = await safety.Journal.ListAsync(safety.StoreRoot);
        if (listing.Operations.Count == 0) { _status.Localized(() => Lang.Get("md_folder_history_empty") + string.Join("；", listing.Errors)); return; }
        var window = new Window { Title = Lang.Get("md_folder_history_title"), Width = 850, Height = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var list = new ListBox { ItemsSource = listing.Operations.OrderByDescending(o => o.CreatedUtc).ToArray(), ItemTemplate = new FuncDataTemplate<SyncOperation>((o, _) => new TextBlock { Text = o is null ? "" : $"{o.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {SyncOperationStateText(o.State)} · {o.LeftRoot} ↔ {o.RightRoot}", Margin = new Thickness(6) }) };
        list.DoubleTapped += (_, _) => { if (list.SelectedItem is SyncOperation operation) window.Close(operation); }; window.Content = list;
        var selected = await window.ShowDialog<SyncOperation?>(this); if (selected is null) return;
        var recovery = Plugin<ISyncRecovery>(); var plan = await recovery.PreviewAsync(safety, selected.Id);
        string restore = Lang.Get("md_folder_restore_run");
        if (await Ask(Lang.Get("md_folder_history_preview_title"), string.Join("\n", plan.Items.Select(i =>
                $"{Lang.Get(i.CanRestore ? "md_folder_restorable" : "md_folder_skipped")} {i.RelativePath}: {Lang.Text(i.Reason)}")), [restore, Lang.Get("dlg_cancel")]) != restore) return;
        if (!await Leave()) return;
        var result = await recovery.RestoreAsync(safety, selected.Id, plan.Items.Where(i => i.CanRestore).Select(i => i.EntryId).ToArray());
        _session.Clear(); RefreshEditors(); _status.Text = Lang.Get("md_restore_result", result.Restored, result.Conflicts, result.Failed);
    }
}
