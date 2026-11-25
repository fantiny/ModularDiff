using System.Collections.ObjectModel;
using Avalonia;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ModularDiff.Contracts;
using ModularDiff.Core;

namespace ModularDiff.Desktop;

public sealed partial class MainWindow
{
    // ─── 目录视图状态 ───
    // _folderAll 是"行条目"的当前清单:文件 + 只在一侧存在的目录。两侧都有的目录是结构(其下文件逐个列出),
    // 不占行。清单始终按相对路径有序,增量只做最小的插入/替换,列表的滚动位置与选择因此得以保留。
    private readonly List<FileComparison> _folderAll = [];
    private readonly ObservableCollection<FileComparison> _folderRows = [];
    private readonly TextBox _folderSearch = new() { MinWidth = 230 };
    private readonly ToggleButton _folderRegex = new() { Content = ".*", MinWidth = 36, Padding = new Thickness(6, 4) };
    private readonly ComboBox _folderStatusFilter = new() { Width = 118 };
    private readonly TextBlock _folderCount = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Number };
    private FolderNameFilter? _folderNameFilter;
    private int _folderStatus;            // 0 全部 · 1 有差异 · 2 相同 · 3 仅左侧 · 4 仅右侧(与 WPF 的筛选码一致)
    private string? _folderFilterError;
    private string _scanStatus = "", _compareStatus = "";
    private readonly Queue<ComparisonProgress> _folderDeltas = new();
    private readonly DispatcherTimer _folderUiTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private int _folderScanRun = -1;
    private FileComparison? _pickedLeft, _pickedRight;

    /// <summary>目录比较是否已拿到最终结果(扫描期间为 false)。</summary>
    public bool FolderCompareComplete { get; private set; }
    /// <summary>当前筛选下可见的行。</summary>
    public IReadOnlyList<FileComparison> FolderRows => _folderRows.ToArray();
    public int FolderVisibleCount => _folderRows.Count;
    /// <summary>当前清单里的文件总数(不含目录行)。</summary>
    public int FolderTotalFiles => _folderAll.Count(f => !f.IsDirectory);
    /// <summary>名称筛选非法(如无效正则)时的说明;合法时为 null。</summary>
    public string? FolderFilterError => _folderFilterError;
    public string StatusText => _status.Text ?? "";
    public string? SessionLeft => _session.Left?.Path;
    public string? SessionRight => _session.Right?.Path;

    private void InitializeFolderView()
    {
        _folderSearch.TextChanged += (_, _) => ApplyFolderFilter();   // 每次按键即时筛选
        Tip(_folderSearch, "folder_search_tip"); Tip(_folderRegex, "folder_regex_name");
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, Children = { _folderSearch, _folderRegex, _folderStatusFilter, _folderCount } };
        for (int i = 0; i < bar.Children.Count; i++) { if (bar.Children[i] is Control control) control.Margin = new Thickness(0, 0, 8, 0); }
        _folderRegex.IsCheckedChanged += (_, _) => ApplyFolderFilter();
        _folderStatusFilter.SelectionChanged += (_, _) =>
        {
            if (_folderStatusFilter.SelectedIndex >= 0) _folderStatus = _folderStatusFilter.SelectedIndex;   // 重建 ItemsSource 时的 -1 不算选择
            ApplyFolderFilter();
        };
        var pane = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(0, 6, 0, 0) };
        pane.Children.Add(bar); Grid.SetRow(_files, 1); pane.Children.Add(_files);
        _folderTab.Content = pane;
        _files.ItemsSource = _folderRows;
        _files.ItemTemplate = BuildFolderRowTemplate();
        _files.DoubleTapped += (_, _) => Run(OpenFolderSelection);
        _files.AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(_files).Properties.PointerUpdateKind != Avalonia.Input.PointerUpdateKind.RightButtonReleased) return;
            // 右键到未选中的行 = 先选中它;右键到空白处 = 不弹上一次残留的菜单。
            FileComparison[] selected = _files.SelectedItems?.Cast<FileComparison>().ToArray() ?? [];
            if (selected.Length == 0)
            {
                _files.ContextMenu = null;
                return;
            }
            _files.ContextMenu = BuildFolderMenu(selected);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _folderUiTimer.Tick += (_, _) => DrainFolderDeltas();
        LocalizeFolderView();
    }

    /// <summary>行模板按当前语言生成文本;语言切换时整体重建,已显示的行才会跟着换语言。</summary>
    private Avalonia.Controls.Templates.FuncDataTemplate<FileComparison> BuildFolderRowTemplate() => new((file, _) =>
        file is null ? new TextBlock() : Columns(
            new TextBlock { Text = FolderRowText(file, left: true), Margin = new Thickness(4), FontFamily = new FontFamily("Cascadia Code,Menlo,DejaVu Sans Mono,monospace"), FontSize = 12 },
            new TextBlock { Text = FolderRowText(file, left: false), Margin = new Thickness(4), FontFamily = new FontFamily("Cascadia Code,Menlo,DejaVu Sans Mono,monospace"), FontSize = 12 }, false));

    private void LocalizeFolderView()
    {
        _folderSearch.Watermark = Lang.Get("folder_search_name");
        _folderStatusFilter.ItemsSource = new[] { Lang.Get("folder_filter_all"), Lang.Get("folder_filter_diff"), Lang.Get("folder_filter_same"), Lang.Get("folder_filter_left"), Lang.Get("folder_filter_right") };
        _folderStatusFilter.SelectedIndex = _folderStatus;
        _files.ItemTemplate = BuildFolderRowTemplate();
        UpdateFolderCount();
    }

    private static bool IsFolderRow(FileComparison entry) =>
        !entry.IsDirectory || entry.Status is FileStatus.LeftOnly or FileStatus.RightOnly;

    // 键名不能由枚举名机械拼出:LeftOnly 对应的是 file_status_left_only(不是 file_status_leftonly)。
    // 机械拼接曾让单侧条目在界面上显示成原始键名 —— 单元测试只断言了"相同"状态,是视觉验收发现的。
    public static string FolderStatusText(FileStatus status) => Lang.Get(status switch
    {
        FileStatus.Equal => "file_status_equal",
        FileStatus.Error => "file_status_error",
        FileStatus.LeftOnly => "file_status_left_only",
        FileStatus.Modified => "file_status_modified",
        FileStatus.Pending => "file_status_pending",
        FileStatus.RightOnly => "file_status_right_only",
        _ => "file_status_error",
    });
    private static string EntryName(FileComparison entry) => Path.GetFileName(entry.RelativePath);

    /// <summary>目录行某一侧显示的文本:状态用界面语言(不再输出枚举名),后跟路径、大小与修改时间、备注。</summary>
    public string FolderRowText(FileComparison file, bool left)
    {
        if (left ? file.Status == FileStatus.RightOnly : file.Status == FileStatus.LeftOnly) return "—";
        var text = new StringBuilder().Append(FolderStatusText(file.Status)).Append("  ").Append(file.RelativePath);
        if (file.IsDirectory) text.Append('/');
        string? root = left ? _directory?.LeftPath : _directory?.RightPath;
        if (!file.IsDirectory && !string.IsNullOrEmpty(root))
        {
            string info = DescribeFile(Path.Combine(root, file.RelativePath));
            if (info.Length > 0) text.Append("   ").Append(info);
        }
        if (!string.IsNullOrEmpty(file.Note)) text.Append("   · ").Append(Lang.Text(file.Note));
        return text.ToString();
    }

    private static string DescribeFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return "";
            string size = info.Length < 1024 ? $"{info.Length} B"
                : info.Length < 1024L * 1024 ? $"{info.Length / 1024d:0.#} KB"
                : info.Length < 1024L * 1024 * 1024 ? $"{info.Length / (1024d * 1024):0.#} MB" : $"{info.Length / (1024d * 1024 * 1024):0.##} GB";
            return string.Create(CultureInfo.InvariantCulture, $"{size} · {info.LastWriteTime:yyyy-MM-dd HH:mm}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return ""; }
    }

    // ─── 名称筛选 / 状态筛选 ───
    public void SetFolderFilter(string query, bool regexMode, int statusFilter)
    {
        _folderRegex.IsChecked = regexMode;
        _folderStatus = statusFilter;
        _folderStatusFilter.SelectedIndex = statusFilter;
        _folderSearch.Text = query;
        ApplyFolderFilter();   // 控件值未变时不会触发事件,这里兜底应用一次
    }

    private void ApplyFolderFilter()
    {
        // 过滤器一律由控件当前值生成:三种入口(键入/切换正则/程序化设置)走同一条路。
        _folderNameFilter = FolderNameFilter.Create(_folderSearch.Text ?? "", _folderRegex.IsChecked == true);
        _folderFilterError = _folderNameFilter.Error is { } error ? Lang.Text(error) : null;
        RefreshFolderView(keepSelection: true);
    }

    private bool PassesFolderFilter(FileComparison entry) => _folderStatus switch
    {
        1 => entry.Status != FileStatus.Equal,
        2 => entry.Status == FileStatus.Equal,
        3 => entry.Status == FileStatus.LeftOnly,
        4 => entry.Status == FileStatus.RightOnly,
        _ => true,
    };

    /// <summary>把可见行集合同步成"当前清单经过筛选"的结果:只做最小的插入/移除/替换。</summary>
    private void RefreshFolderView(bool keepSelection)
    {
        var keep = keepSelection ? _files.SelectedItems?.Cast<FileComparison>().Select(f => f.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        var filter = _folderNameFilter;
        var desired = _folderAll.Where(entry => PassesFolderFilter(entry) && (filter is null || filter.Matches(entry.RelativePath, EntryName(entry)))).ToArray();
        SyncRows(_folderRows, desired);
        if (keep is { Count: > 0 })
            foreach (var row in _folderRows)
                if (keep.Contains(row.RelativePath)) _files.SelectedItems?.Add(row);
        UpdateFolderCount();
    }

    private static void SyncRows(ObservableCollection<FileComparison> target, IReadOnlyList<FileComparison> desired)
    {
        var wanted = new HashSet<string>(desired.Select(f => f.RelativePath), StringComparer.OrdinalIgnoreCase);
        for (int i = target.Count - 1; i >= 0; i--)
            if (!wanted.Contains(target[i].RelativePath)) target.RemoveAt(i);
        var position = new Dictionary<string, int>(target.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < target.Count; i++) position[target[i].RelativePath] = i;
        for (int i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            if (position.TryGetValue(item.RelativePath, out int at))
            {
                if (at == i) { if (!ReferenceEquals(target[i], item)) target[i] = item; }
                else
                {
                    var moving = target[at];
                    target.RemoveAt(at);
                    target.Insert(i, ReferenceEquals(moving, item) ? moving : item);
                    for (int j = i; j <= at; j++) if (j < target.Count) position[target[j].RelativePath] = j;
                }
            }
            else { target.Insert(i, item); for (int j = i; j < target.Count; j++) position[target[j].RelativePath] = j; }
        }
    }

    private void UpdateFolderCount() =>
        _folderCount.Text = Lang.Get("folder_visible_summary", _folderRows.Count(f => !f.IsDirectory), FolderTotalFiles);

    // ─── 渐进式扫描:增量应用 ───
    private void EnqueueFolderProgress(ComparisonProgress progress)
    {
        lock (_folderDeltas) _folderDeltas.Enqueue(progress);
    }

    private void StartFolderScan(int run)
    {
        _folderScanRun = run;
        _scanStatus = _compareStatus = "";
        FolderCompareComplete = false;
        lock (_folderDeltas) _folderDeltas.Clear();
        if (!_folderUiTimer.IsEnabled) _folderUiTimer.Start();
    }

    private void StopFolderScan()
    {
        _folderUiTimer.Stop();
        lock (_folderDeltas) _folderDeltas.Clear();   // 未处理的增量作废:最终结果才是权威
    }

    /// <summary>每 120ms 把这段时间的增量合并应用到列表(不逐条刷新界面),状态栏每拍只写一次。</summary>
    private void DrainFolderDeltas()
    {
        if (_folderScanRun != _compareRun || _busy is false) { StopFolderScan(); return; }
        List<ComparisonProgress> batch;
        lock (_folderDeltas)
        {
            batch = new List<ComparisonProgress>(Math.Min(_folderDeltas.Count, 4000));
            while (batch.Count < 4000 && _folderDeltas.TryDequeue(out var progress)) batch.Add(progress);
        }
        if (batch.Count == 0) return;
        foreach (var progress in batch) ApplyFolderProgressCore(progress);
        RefreshFolderView(keepSelection: true);
        _status.Localized(FolderProgressText);
    }

    private string FolderProgressText() =>
        string.Join(" · ", new[] { _scanStatus, _compareStatus }.Where(s => s.Length > 0));

    /// <summary>应用一条进度:根波快照替换清单,Added 只新增(迟到的占位绝不会覆盖已解析的行),
    /// FileUpdate 原地解析。对顺序不敏感,所以即使宿主用异步投递的进度接收者也不会出错。</summary>
    public void ApplyFolderProgress(ComparisonProgress progress)
    {
        ApplyFolderProgressCore(progress);
        RefreshFolderView(keepSelection: true);
        _status.Localized(FolderProgressText);
    }

    private void ApplyFolderProgressCore(ComparisonProgress progress)
    {
        if (progress.PartialResult is { } partial && Directory.Exists(partial.LeftPath))
        {
            _directory = partial;
            _folderAll.Clear();
            foreach (var entry in partial.Files) if (IsFolderRow(entry)) InsertFolderEntry(entry);
            if (!Equals(_tabs.SelectedIndex, 2)) _tabs.SelectedIndex = 2;
        }
        if (progress.Added is { } added)
            foreach (var entry in added) if (IsFolderRow(entry) && IndexOfFolderEntry(entry.RelativePath) < 0) InsertFolderEntry(entry);
        if (progress.FileUpdate is { } update)
        {
            int at = IndexOfFolderEntry(update.RelativePath);
            if (IsFolderRow(update)) { if (at >= 0) _folderAll[at] = update; else InsertFolderEntry(update); }
            else if (at >= 0) _folderAll.RemoveAt(at);
        }
        if (progress.Path.StartsWith("scan:", StringComparison.Ordinal))
            _scanStatus = progress.Path == "scan:root" ? Lang.Get("folder_scan_root") : Lang.Get("folder_scan_progress", progress.Completed);
        else if (progress.Total > 0)
            _compareStatus = Lang.Get("folder_compare_progress", progress.Completed, progress.Total, progress.Path);
    }

    private int IndexOfFolderEntry(string relativePath)
    {
        int low = 0, high = _folderAll.Count;   // 清单按相对路径有序:二分
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (string.Compare(_folderAll[mid].RelativePath, relativePath, StringComparison.OrdinalIgnoreCase) < 0) low = mid + 1; else high = mid;
        }
        return low < _folderAll.Count && string.Equals(_folderAll[low].RelativePath, relativePath, StringComparison.OrdinalIgnoreCase) ? low : -1;
    }

    private void InsertFolderEntry(FileComparison entry)
    {
        int low = 0, high = _folderAll.Count;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (string.Compare(_folderAll[mid].RelativePath, entry.RelativePath, StringComparison.OrdinalIgnoreCase) < 0) low = mid + 1; else high = mid;
        }
        _folderAll.Insert(low, entry);
    }

    /// <summary>最终结果:清单重建为权威内容,选中项按路径保留,状态栏给出汇总。</summary>
    private void ApplyFolderResult(ComparisonResult result, bool switchToTab = true)
    {
        StopFolderScan();
        _directory = result;
        FolderCompareComplete = true;
        _scanStatus = _compareStatus = "";
        var keep = _files.SelectedItems?.Cast<FileComparison>().Select(f => f.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _folderAll.Clear();
        foreach (var entry in result.Files) if (IsFolderRow(entry)) InsertFolderEntry(entry);
        _pickedLeft = _pickedRight = null;
        RefreshFolderView(keepSelection: false);
        if (keep is { Count: > 0 })
            foreach (var row in _folderRows) if (keep.Contains(row.RelativePath)) _files.SelectedItems?.Add(row);
        if (switchToTab) _tabs.SelectedIndex = 2;
        var files = result.Files.Where(f => !f.IsDirectory).ToArray();
        int equal = files.Count(f => f.Status == FileStatus.Equal), different = files.Count(f => f.Status is FileStatus.Modified or FileStatus.LeftOnly or FileStatus.RightOnly), errors = files.Count(f => f.Status == FileStatus.Error);
        _status.Localized(() => Lang.Get("status_summary", files.Length, equal, different, errors));
    }

    // ─── 选择 → 文件集合(选中目录行 = 其下全部文件)───
    private FileComparison[] SelectedFolderFiles()
    {
        var selected = _files.SelectedItems?.Cast<FileComparison>().ToArray() ?? [];
        var files = new List<FileComparison>();
        foreach (var entry in selected)
        {
            if (!entry.IsDirectory) { files.Add(entry); continue; }
            string prefix = entry.RelativePath + Path.DirectorySeparatorChar;
            files.AddRange(_directory?.Files.Where(f => !f.IsDirectory && f.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                ?? Enumerable.Empty<FileComparison>());
        }
        return files.DistinctBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task OpenFolderSelection()
    {
        if (_directory is null || !FolderCompareComplete) return;   // 扫描期间不打开(最终结果到达时会替换会话)
        var first = SelectedFolderFiles().FirstOrDefault();
        if (first is null || !await Leave()) return;
        if (first.IsBinary || first.Status == FileStatus.Error)
        {
            _status.Localized(() => string.IsNullOrEmpty(first.Note) ? Lang.Get("md_folder_binary_hint") : $"{Lang.Get("md_folder_binary_hint")} {Lang.Text(first.Note)}");
            return;
        }
        await OpenFile(Path.Combine(_directory.LeftPath, first.RelativePath), Path.Combine(_directory.RightPath, first.RelativePath));
    }

    // ─── 跨文件打开:左侧 A ↔ 右侧 B(改名后的文件也能从列表里配对比较)───
    public void PickFolderSide(bool left, FileComparison row)
    {
        if (row.IsDirectory || _directory is null) return;
        if (left) _pickedLeft = row; else _pickedRight = row;
        var picked = (Left: _pickedLeft, Right: _pickedRight); string side = left ? "side_left" : "side_right"; var row_ = row;
        _status.Localized(() => picked.Left is not null && picked.Right is not null
            ? Lang.Get("status_will_compare", Path.Combine(_directory.LeftPath, picked.Left.RelativePath), Path.Combine(_directory.RightPath, picked.Right.RelativePath))
            : Lang.Get("md_folder_pick_half", Lang.Get(side), row_.RelativePath));
    }

    public (string Left, string Right)? PickedPair => _pickedLeft is { } l && _pickedRight is { } r && _directory is { } dir
        ? (Path.Combine(dir.LeftPath, l.RelativePath), Path.Combine(dir.RightPath, r.RelativePath)) : null;

    public async Task OpenPickedPair()
    {
        if (PickedPair is not { } pair) { _status.Localized(() => Lang.Get("md_folder_pick_need_both")); return; }
        if (!await Leave()) return;
        await OpenFile(pair.Left, pair.Right);
    }

    private ContextMenu BuildFolderMenu(FileComparison[] selected)
    {
        var menu = new ContextMenu();
        MenuItem Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }
        var open = Add(Lang.Get("menu_open_compare"), () => Run(OpenFolderSelection));
        open.IsEnabled = FolderCompareComplete;
        menu.Items.Add(new Separator());
        if (selected.Length == 1 && !selected[0].IsDirectory)
        {
            Add(Lang.Get("md_folder_pick_left"), () => PickFolderSide(left: true, selected[0]));
            Add(Lang.Get("md_folder_pick_right"), () => PickFolderSide(left: false, selected[0]));
        }
        var pair = Add(Lang.Get("md_folder_open_pair"), () => Run(OpenPickedPair));
        pair.IsEnabled = PickedPair is not null;
        menu.Items.Add(new Separator());
        var files = SelectedFolderFiles();
        if (files.Length > 0)
        {
            Add(Lang.Get("folder_copy_n", files.Length, Lang.Get("side_right")), () => Run(() => CopySelectedFolderFiles(true)));
            Add(Lang.Get("folder_copy_n", files.Length, Lang.Get("side_left")), () => Run(() => CopySelectedFolderFiles(false)));
            menu.Items.Add(new Separator());
            Add(Lang.Get("sync_sel_right_tip"), () => Run(() => Sync(SyncMode.LeftToRight)));
            Add(Lang.Get("sync_sel_left_tip"), () => Run(() => Sync(SyncMode.RightToLeft)));
        }
        return menu;
    }

    // ─── 同步:范围、预览与结果文本 ───
    /// <summary>同步目标的相对路径:选中的文件(目录行展开为其下文件);未选择时为全部有差异的文件。目录本身永不是目标。</summary>
    public string[] SyncTargetPaths()
    {
        if (_directory is null) throw new IOException(Lang.Get("md_folder_need_compare"));
        var selected = SelectedFolderFiles();
        var source = selected.Length > 0 ? selected : _directory.Files.Where(f => !f.IsDirectory && f.Status != FileStatus.Equal);
        return source.Select(f => f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>同步预览的每一行:动作与状态用界面语言(插件发出的是 Lang 键),备注与两侧信息经 messages 表翻译。</summary>
    public async Task<IReadOnlyList<string>> DescribeSync(SyncMode mode)
    {
        var targets = SyncTargetPaths();
        if (targets.Length == 0) return [];
        var plan = await Plugin<ISyncPlanner>().PlanAsync(_directory!.LeftPath, _directory.RightPath, targets, mode);
        return FormatSyncPlan(Plugin<ISyncPreview>().Describe(plan));
    }

    private IReadOnlyList<string> FormatSyncPlan(IReadOnlyList<SyncPreviewItem> items) => items.Select(item =>
        {
            string? action = item.Choices.FirstOrDefault(c => c.Action == item.SuggestedAction)?.Label;
            var parts = new List<string> { action is null ? Lang.Get("sync_choice_skip") : Lang.PluginText(action), item.RelativePath, Lang.PluginText(item.State) };
            string left = item.LeftInfo == "sync_stamp_missing" ? Lang.Get("sync_stamp_missing") : Lang.Text(item.LeftInfo);
            string right = item.RightInfo == "sync_stamp_missing" ? Lang.Get("sync_stamp_missing") : Lang.Text(item.RightInfo);
            parts.Add($"({Lang.Get("side_left")} {left} | {Lang.Get("side_right")} {right})");
            return string.Join("  ", parts);
        }).ToList();

    public string SyncResultSummary(SyncResult result) => Lang.Get("sync_result_summary",
        result.Entries.Count(e => e.Outcome == SyncOutcome.Copied), result.Entries.Count(e => e.Outcome == SyncOutcome.Failed),
        result.Entries.Count(e => e.Outcome == SyncOutcome.Skipped), result.Entries.Count(e => e.Outcome == SyncOutcome.Cancelled));

    public IReadOnlyList<string> DescribeSyncResult(SyncResult result) =>
        result.Entries.Select(e => $"{e.RelativePath}: {Lang.Text(e.Message)}").ToList();

    public string SyncOperationStateText(string state) => state switch
    {
        "Running" => Lang.Get("sync_op_running"),
        "Completed" => Lang.Get("sync_op_completed"),
        "Partial" => Lang.Get("sync_op_partial"),
        "Cancelled" => Lang.Get("sync_op_cancelled"),
        "RecoveryPartial" => Lang.Get("sync_op_recovery_partial"),
        "Restored" => Lang.Get("sync_op_restored"),
        _ => state,
    };

    /// <summary>同步回调的进度接收者:在报告线程上就地入队,保留模块给出的先后顺序
    /// (先投递到线程池会打乱顺序,FileUpdate 就可能跑到它所属条目的 Added 前面)。</summary>
    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    // ─── 路径栏:回车即比较(经 Core 校验,错误用界面语言说明)───
    public void SetPathBars(string? left, string? right) { _leftPath.Text = left; _rightPath.Text = right; }
    public (string? Left, string? Right) PathBarTexts => (_leftPath.Text, _rightPath.Text);
    /// <summary>左右路径输入框(测试用来发真实的 Enter 按键事件)。</summary>
    public IReadOnlyList<TextBox> PathBarBoxes => [_leftPath, _rightPath];

    public bool CommitPathBars()
    {
        if (_busy) return false;
        if (!PathBarCommit.TryResolve(_leftPath.Text, _rightPath.Text, out string left, out string right, out string? error))
        {
            _status.Localized(() => error is null ? Lang.Get("status_path_restored") : Lang.Text(error));
            return false;
        }
        // 规范成不带尾部分隔符的绝对路径:之后"同路径重对齐"、会话保存等按字符串比较路径的逻辑才有稳定依据
        // ("L/" 与 "L" 会被当成两个不同的路径)。
        _leftPath.Text = Path.TrimEndingDirectorySeparator(left); _rightPath.Text = Path.TrimEndingDirectorySeparator(right);
        Run(Compare);
        return true;
    }
}
