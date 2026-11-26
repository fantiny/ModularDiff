using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ModularDiff.Contracts;
using ModularDiff.Core;

namespace ModularDiff.Desktop;

/// <summary>目录同步预览:每个文件一行,复制方向逐项可改,跳过的不写入 —— 与 WPF 的 SyncPreviewWindow 同约定。
/// 此前 macOS 只能把计划列成文字、按插件建议的方向整体执行,用户无法对单个文件说"这个不要动"。
/// 插件给出的动作名和状态是语言键,在这里按界面语言显示。</summary>
public sealed class SyncPreviewWindow : Window
{
    /// <summary>一个文件,和它当前选定的动作。文字都按读取时的界面语言给出。</summary>
    public sealed class Row(SyncPreviewItem item, SyncPlan plan)
    {
        public SyncPreviewItem Item { get; } = item;
        public IReadOnlyList<SyncActionChoice> Choices { get; } = item.Choices;
        public SyncAction Action { get; set; } = item.SuggestedAction;
        public string RelativePath => Item.RelativePath;
        public string State => Lang.PluginText(Item.State);
        public string LeftInfo => InfoText(Item.LeftInfo);
        public string RightInfo => InfoText(Item.RightInfo);

        /// <summary>这项动作会盖掉目标侧已有的文件(汇总里算"覆盖",而不是"新增")。</summary>
        public bool Overwrites
        {
            get
            {
                if (Action == SyncAction.Skip) return false;
                var file = plan.Items.FirstOrDefault(entry => string.Equals(entry.RelativePath, Item.RelativePath, StringComparison.OrdinalIgnoreCase));
                return file is not null && (Action == SyncAction.LeftToRight ? file.Right.Exists : file.Left.Exists);
            }
        }
    }

    private readonly List<Row> _rows;
    private readonly Dictionary<string, ComboBox> _boxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock _summary = new();
    private readonly Button _run = new() { MinWidth = 100 };

    public IReadOnlyList<Row> Rows => _rows;
    /// <summary>照现在的选择执行会发生什么:多少项、几个新增、几个覆盖、几个跳过。</summary>
    public string SummaryText => _summary.Text ?? "";
    public bool CanRun => _run.IsEnabled;
    /// <summary>每一行的决定,按行的顺序 —— 执行时交给同步插件的就是它。</summary>
    public IReadOnlyList<SyncDecision> Decisions => _rows.Select(row => new SyncDecision(row.RelativePath, row.Action)).ToArray();

    public SyncPreviewWindow(SyncPlan plan, IReadOnlyList<SyncPreviewItem> items, string? scope = null)
    {
        _rows = items.Select(item => new Row(item, plan)).ToList();
        Title = Lang.Get("md_folder_sync_preview_title");
        Width = 1040; Height = 560; MinWidth = 760; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto"), Margin = new Thickness(14), RowSpacing = 6 };
        int row = 0;
        void Add(Control control, int index) { Grid.SetRow(control, index); root.Children.Add(control); }
        Add(new TextBlock { Text = Lang.Get("md_folder_sync_intro"), TextWrapping = TextWrapping.Wrap }, row++);
        Add(new TextBlock { Text = Lang.Get("sync_roots", plan.LeftRoot, plan.RightRoot), Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis }, row++);
        if (scope is not null) Add(new TextBlock { Text = scope, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis }, row++);

        var header = new Grid { ColumnDefinitions = Columns() };
        string[] captions = [Lang.Get("sync_col_action"), Lang.Get("sync_col_relpath"), Lang.Get("sync_col_left"), Lang.Get("sync_col_right"), Lang.Get("sync_col_state")];
        for (int column = 0; column < captions.Length; column++)
        {
            var caption = new TextBlock { Text = captions[column], FontWeight = FontWeight.SemiBold, Margin = new Thickness(4, 2, 4, 2) };
            Grid.SetColumn(caption, column); header.Children.Add(caption);
        }
        Add(header, row++);

        var list = new StackPanel { Orientation = Orientation.Vertical };
        foreach (var item in _rows) list.Children.Add(BuildRow(item));
        Add(new ScrollViewer { Content = list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, row++);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        _summary.VerticalAlignment = VerticalAlignment.Center; _summary.Margin = new Thickness(0, 8, 12, 0);
        footer.Children.Add(_summary);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        _run.Content = Lang.Get("md_folder_sync_run");
        ToolTip.SetTip(_run, Lang.Get("sync_preview_execute_tip"));
        _run.Click += (_, _) => Close(Decisions);
        var cancel = new Button { Content = Lang.Get("dlg_cancel"), MinWidth = 84, IsCancel = true };
        ToolTip.SetTip(cancel, Lang.Get("sync_preview_close_tip"));
        cancel.Click += (_, _) => Close(null);
        buttons.Children.Add(_run); buttons.Children.Add(cancel);
        Grid.SetColumn(buttons, 1); footer.Children.Add(buttons);
        Add(footer, row);
        Content = root;
        UpdateSummary();
    }

    /// <summary>The columns of the header and of every row. Each grid needs its own instance: the collection belongs to one grid.</summary>
    private static ColumnDefinitions Columns() => new("180,2*,1.6*,1.6*,1.3*");

    private Control BuildRow(Row row)
    {
        var grid = new Grid { ColumnDefinitions = Columns(), Margin = new Thickness(0, 3, 0, 3) };
        var box = new ComboBox { ItemsSource = row.Choices.Select(choice => Lang.PluginText(choice.Label)).ToArray(), HorizontalAlignment = HorizontalAlignment.Stretch };
        ToolTip.SetTip(box, Lang.Get("sync_choice_tip"));
        // 只认列表里真有的序号:清空选择(序号 -1)不是一种动作。
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex < 0 || box.SelectedIndex >= row.Choices.Count) return;
            row.Action = row.Choices[box.SelectedIndex].Action;
            UpdateSummary();
        };
        box.SelectedIndex = Array.FindIndex(row.Choices.ToArray(), choice => choice.Action == row.Action);
        grid.Children.Add(box);
        var cells = new[] { row.RelativePath, row.LeftInfo, row.RightInfo, row.State };
        for (int column = 0; column < cells.Length; column++)
        {
            var cell = new TextBlock { Text = cells[column], Margin = new Thickness(4, 7, 4, 2), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(cell, cells[column]);
            Grid.SetColumn(cell, column + 1); grid.Children.Add(cell);
        }
        _boxes[row.RelativePath] = box;
        return grid;
    }

    public SyncAction? ActionOf(string relativePath) => _rows.FirstOrDefault(r => string.Equals(r.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))?.Action;

    /// <summary>把一个文件的动作改成 <paramref name="action"/>。插件没有为这个文件提供的动作不改,返回 false。</summary>
    public bool Choose(string relativePath, SyncAction action)
    {
        var row = _rows.FirstOrDefault(r => string.Equals(r.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
        if (row is null || !_boxes.TryGetValue(row.RelativePath, out var box)) return false;
        int index = Array.FindIndex(row.Choices.ToArray(), choice => choice.Action == action);
        if (index < 0) return false;
        box.SelectedIndex = index;   // the list's own change does the rest, as it does for the user
        return row.Action == action;
    }

    private void UpdateSummary()
    {
        var copies = _rows.Where(row => row.Action != SyncAction.Skip).ToArray();
        int overwrite = copies.Count(row => row.Overwrites);
        _summary.Text = Lang.Get("sync_summary", _rows.Count, copies.Length - overwrite, overwrite, _rows.Count - copies.Length);
        _run.IsEnabled = copies.Length > 0;
    }

    /// <summary>一侧的信息:缺失是语言键,大小与时间是插件给出的文本,经 messages 表翻译。</summary>
    internal static string InfoText(string info) => info == "sync_stamp_missing" ? Lang.Get("sync_stamp_missing") : Lang.Text(info);
}
