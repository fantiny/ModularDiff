using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ModularDiff.Contracts;
using ModularDiff.Core;
using ModularDiff.Gui.Controls;

namespace ModularDiff.Gui;

public partial class MainWindow : Window
{
    private readonly PluginCatalog _plugins;
    private readonly ComparisonEngine _engine;
    private readonly string[] _arguments;
    private ComparisonResult? _folderResult, _fileResult;
    private ComparisonRequest? _rootRequest;
    private FolderTree? _tree;
    private IReadOnlyList<FolderNode> _folderRows = [];
    private FolderNode? _currentNode;
    private FileComparison? _textFile;
    private TextPresentation? _presentation;
    private CancellationTokenSource? _comparisonCancellation, _detailCancellation;
    private bool _ready, _busy, _syncText, _syncFolderScroll, _syncFolderSelection;
    private int _presentationVersion, _currentHunk = -1;
    private double _codeFontSize = 14;
    private static int _windowCount;
    private sealed record ModuleChoice(string? Id, string DisplayName);

    public MainWindow(string[] arguments)
    {
        _arguments = arguments;
        InitializeComponent();
        int cascade = Interlocked.Increment(ref _windowCount) - 1;   // 新窗口级联偏移，避免完全叠加难发现
        Left = 70 + (cascade % 12) * 36; Top = 46 + (cascade % 12) * 30;
        _plugins = PluginCatalog.Load(Arg("--plugins") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "plugins")));
        _engine = new(_plugins);
        AlgorithmBox.ItemsSource = _plugins.GetAll<IDiffAlgorithm>();
        AlgorithmBox.SelectedItem = _plugins.GetAll<IDiffAlgorithm>().FirstOrDefault(p => p.Id == "myers");
        ModuleBox.ItemsSource = new[] { new ModuleChoice(null, "自动选择") }.Concat(_plugins.GetAll<IComparisonModule>().Select(p => new ModuleChoice(p.Id, p.DisplayName))).ToArray();
        ModuleBox.SelectedIndex = 0;
        PresentationBox.ItemsSource = _plugins.GetAll<ITextPresentation>();
        PresentationBox.SelectedItem = _plugins.GetAll<ITextPresentation>().FirstOrDefault(p => p.Id == "side-by-side") ?? _plugins.GetAll<ITextPresentation>().FirstOrDefault();
        FormatBox.ItemsSource = _plugins.GetAll<IResultFormatter>(); FormatBox.SelectedIndex = 0;
        LeftPath.Text = Arg("--left") ?? ""; RightPath.Text = Arg("--right") ?? "";
        PluginSummary.Text = $"{_plugins.GetAll<IPlugin>().Count} 个插件已加载";
        LeftCode.ViewChanged += CodeScrolled; RightCode.ViewChanged += CodeScrolled;
        LeftCode.RowSelected += index => CodeSelected(index, LeftCode);
        RightCode.RowSelected += index => CodeSelected(index, RightCode);
        LeftCode.ExpandContext += () => TextFilter.SelectedIndex = 0;
        RightCode.ExpandContext += () => TextFilter.SelectedIndex = 0;
        Overview.NavigateRequested += NavigateSource;
        LeftCode.ShowEndings = RightCode.ShowEndings = false;
        if (_plugins.Diagnostics.Count > 0)
        {
            StatusLabel.Text = "插件加载失败：" + string.Join(" | ", _plugins.Diagnostics);
            CompareButton.IsEnabled = false;
        }
        Closed += (_, _) => { _comparisonCancellation?.Cancel(); _detailCancellation?.Cancel(); };
        InitializeEditing(); InitializeSync(); InitializeLineTools(); InitializeSessions(); InitializeFolderOps();
        _ready = true;
        if (double.TryParse(Arg("--smoke-width"), out double width)) Width = Math.Max(MinWidth, width);
        if (arguments.Contains("--sync-smoke-test")) Loaded += SyncSmokeTest;
        else if (arguments.Contains("--edit-smoke-test")) Loaded += EditSmokeTest;
        else if (arguments.Contains("--smoke-test")) Loaded += SmokeTest;
        else if (arguments.Contains("--auto-compare")) Loaded += async (_, _) => await CompareAsync();
        if (arguments.Contains("--wrap")) WrapToggle.IsChecked = true;
    }
    private string? Arg(string name) { int i = Array.IndexOf(_arguments, name); return i >= 0 && i + 1 < _arguments.Length ? _arguments[i + 1] : null; }
    private void NewWindowClick(object sender, RoutedEventArgs e) => new MainWindow([]).Show();
    private void MergeWindowClick(object sender, RoutedEventArgs e) => new MergeWindow().Show();

    /// <summary>One regex per line; invalid patterns fail the comparison with a clear message.</summary>
    private IReadOnlyList<string>? ParseUnimportantPatterns()
    {
        if (UnimportantBox.Text.Length == 0) return null;
        var patterns = UnimportantBox.Text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        foreach (string pattern in patterns)
        {
            try { _ = new System.Text.RegularExpressions.Regex(pattern); }
            catch (ArgumentException ex) { throw new InvalidOperationException($"不重要差异规则无效：{pattern}（{ex.Message}）"); }
        }
        return patterns.Length > 0 ? patterns : null;
    }
    private void BrowseFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择比较文件" };
        if (dialog.ShowDialog(this) == true) Target(sender).Text = dialog.FileName;
    }
    private void BrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择比较目录" };
        if (dialog.ShowDialog(this) == true) Target(sender).Text = dialog.FolderName;
    }
    private TextBox Target(object sender) => (string)((Button)sender).Tag == "left" ? LeftPath : RightPath;
    private async void CompareClick(object sender, RoutedEventArgs e) => await CompareAsync();
    private async Task CompareAsync()
    {
        if (_busy || !await CanLeaveEdits()) return;
        ClearEditors();
        _detailCancellation?.Cancel();
        _comparisonCancellation?.Dispose(); _comparisonCancellation = new();
        var token = _comparisonCancellation.Token;
        _busy = true; CompareButton.IsEnabled = false; CancelButton.IsEnabled = true; ExportButton.IsEnabled = false;
        _folderResult = _fileResult = null; _rootRequest = null; _tree = null; _currentNode = null; _textFile = null;
        _presentationVersion++; _presentation = null; _folderRows = [];
        LeftFolders.ItemsSource = RightFolders.ItemsSource = null;
        LeftCode.SetRows([]); RightCode.SetRows([]); Overview.SetPresentation(null);
        HexHost.Visibility = Visibility.Collapsed; PictureHost.Visibility = Visibility.Collapsed;
        TextTab.IsEnabled = false; FolderTab.IsEnabled = true; SetView(false);
        ActivityLabel.Text = "比较中…";
        try
        {
            if (string.IsNullOrWhiteSpace(LeftPath.Text) || string.IsNullOrWhiteSpace(RightPath.Text)) throw new InvalidOperationException("请填写左右两侧路径。");
            if (AlgorithmBox.SelectedItem is not IDiffAlgorithm algorithm) throw new InvalidOperationException("没有可用的算法插件。");
            var request = new ComparisonRequest(Path.GetFullPath(LeftPath.Text), Path.GetFullPath(RightPath.Text), algorithm.Id,
                new(IgnoreCaseBox.IsChecked == true, IgnoreWhitespaceBox.IsChecked == true, IgnoreEolBox.IsChecked == true, RecursiveBox.IsChecked == true,
                    UnimportantPatterns: ParseUnimportantPatterns()));
            string? module = (ModuleBox.SelectedItem as ModuleChoice)?.Id;
            var progress = new Progress<ComparisonProgress>(p => { if (_busy && !token.IsCancellationRequested) StatusLabel.Text = $"比较中 {p.Completed}/{p.Total} · {p.Path}"; });
            StatusLabel.Text = "扫描并比较…";
            var result = await Task.Run(() => _engine.CompareAsync(request, module, progress, token), token);
            _rootRequest = request;
            if (Directory.Exists(request.LeftPath) && Directory.Exists(request.RightPath))
            {
                var tree = await Task.Run(() => FolderTree.Build(result, token), token);
                _folderResult = result; _tree = tree; RefreshFolderRows(); SetView(false);
            }
            else
            {
                _fileResult = result; _textFile = result.Files.Single();
                SetTextHeaders(result.LeftPath, result.RightPath, _textFile);
                await RebuildPresentation(token); await AttachEditors(result.LeftPath, result.RightPath, token); TextTab.IsEnabled = true; FolderTab.IsEnabled = false; SetView(true); LeftCode.Focus();
            }
            ExportButton.IsEnabled = true;
            UpdateSummary();
            RecordRecentComparison(request);
        }
        catch (OperationCanceledException) { StatusLabel.Text = "已取消比较。"; }
        catch (Exception ex) { StatusLabel.Text = "比较失败：" + ex.Message; }
        finally
        {
            _busy = false; CompareButton.IsEnabled = _plugins.Diagnostics.Count == 0; CancelButton.IsEnabled = false; ActivityLabel.Text = "就绪";
        }
    }
    /// <summary>File facts for the status bar: line counts, encoding and line-ending style per side.</summary>
    private void UpdateFileStats()
    {
        if (_leftBuffer is not null && _rightBuffer is not null)
        {
            static string Describe(IEditBuffer buffer) =>
                $"{DocumentLines.Parse(buffer.Text).Count:N0} 行 · {buffer.Snapshot().Encoding.Name} · {(buffer.NewLine == "\r\n" ? "CRLF" : buffer.NewLine == "\r" ? "CR" : "LF")}";
            FileStatsLabel.Text = $"左 {Describe(_leftBuffer)} ｜ 右 {Describe(_rightBuffer)}";
        }
        else FileStatsLabel.Text = "";
    }

    private void UpdateSummary()
    {
        var result = TextView.Visibility == Visibility.Visible ? _fileResult : _folderResult;
        if (result is null) return;
        StatusLabel.Text = $"{result.Files.Count} 个文件 · {result.Files.Count(f => f.Status == FileStatus.Equal)} 相同 · {result.Files.Count(f => f.Status is FileStatus.Modified or FileStatus.LeftOnly or FileStatus.RightOnly)} 差异 · {result.Files.Count(f => f.Status == FileStatus.Error)} 错误";
    }
    private void CancelClick(object sender, RoutedEventArgs e) { _syncCancellation?.Cancel(); _comparisonCancellation?.Cancel(); _detailCancellation?.Cancel(); }
    private async void SwapClick(object sender, RoutedEventArgs e)
    {
        if (_busy || !await CanLeaveEdits()) return;
        ClearEditors();
        (LeftPath.Text, RightPath.Text) = (RightPath.Text, LeftPath.Text);
        if (!string.IsNullOrWhiteSpace(LeftPath.Text) && !string.IsNullOrWhiteSpace(RightPath.Text)) await CompareAsync();
    }
    private void SetView(bool text)
    {
        FolderView.Visibility = text ? Visibility.Collapsed : Visibility.Visible;
        TextView.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
        FolderTab.IsChecked = !text; TextTab.IsChecked = text;
        OpenButton.IsEnabled = !text && _folderResult is not null;
        PreviousFileButton.IsEnabled = NextFileButton.IsEnabled = text && _folderResult is not null;
        ExportButton.IsEnabled = text ? _fileResult is not null : _folderResult is not null;
        UpdateSummary(); UpdateEditState();
    }
    private void FolderTabClick(object sender, RoutedEventArgs e) => SetView(false);
    private void TextTabClick(object sender, RoutedEventArgs e) => SetView(true);
    private void RefreshFolderRows(FolderNode? selected = null)
    {
        if (!_ready || _tree is null) return;
        selected ??= LeftFolders.SelectedItem as FolderNode;
        _folderRows = _tree.Visible(FolderFilter.SelectedIndex, FolderSearch.Text.Trim());
        _syncFolderSelection = true;
        LeftFolders.ItemsSource = RightFolders.ItemsSource = _folderRows;
        var target = selected is not null && _folderRows.Contains(selected) ? selected : _folderRows.FirstOrDefault();
        LeftFolders.SelectedItem = RightFolders.SelectedItem = target;
        _syncFolderSelection = false;
        FolderSummary.Text = $"{_folderRows.Count(n => !n.IsDirectory)} 个可见文件 / {_folderResult?.Files.Count ?? 0} 个文件";
    }
    private void FolderFilterChanged(object sender, SelectionChangedEventArgs e) => RefreshFolderRows();
    private void FolderSearchChanged(object sender, TextChangedEventArgs e) => RefreshFolderRows();
    private void ClearFolderSearch(object sender, RoutedEventArgs e) => FolderSearch.Clear();
    private void ExpandAll(object sender, RoutedEventArgs e) { if (_tree is null) return; foreach (var node in _tree.All) node.IsExpanded = true; RefreshFolderRows(); }
    private void CollapseAll(object sender, RoutedEventArgs e) { if (_tree is null) return; foreach (var node in _tree.All) node.IsExpanded = false; RefreshFolderRows(); }
    private void ToggleFolder(FolderNode node) { if (!node.IsDirectory) return; node.IsExpanded = !node.IsExpanded; RefreshFolderRows(node); }
    private void FolderExpanderClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FolderNode { IsDirectory: true } node) { ToggleFolder(node); e.Handled = true; }
    }
    private void FolderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncFolderSelection) return;
        var source = (DataGrid)sender; var other = ReferenceEquals(source, LeftFolders) ? RightFolders : LeftFolders;
        _syncFolderSelection = true;
        other.SelectedItems.Clear();
        foreach (var selected in source.SelectedItems) other.SelectedItems.Add(selected);
        _syncFolderSelection = false;
        if (source.SelectedItem is FolderNode node) StatusLabel.Text = node.Description;
    }
    private void FolderScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (!_ready || _syncFolderScroll || e.VerticalChange == 0 || e.OriginalSource is not ScrollViewer source) return;
        var target = FindChild<ScrollViewer>(ReferenceEquals(sender, LeftFolders) ? RightFolders : LeftFolders);
        if (target is null) return;
        _syncFolderScroll = true; target.ScrollToVerticalOffset(source.VerticalOffset); _syncFolderScroll = false;
    }
    private async void FolderDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindParent<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is FolderNode node)
        { if (node.IsDirectory) ToggleFolder(node); else await OpenNode(node); }
    }
    private async void FolderKeyDown(object sender, KeyEventArgs e)
    {
        if (((DataGrid)sender).SelectedItem is not FolderNode node) return;
        if (e.Key == Key.Enter) { e.Handled = true; if (node.IsDirectory) ToggleFolder(node); else await OpenNode(node); }
        else if (node.IsDirectory && e.Key is Key.Left or Key.Right)
        { node.IsExpanded = e.Key == Key.Right; RefreshFolderRows(node); e.Handled = true; }
    }
    private async void OpenSelectedClick(object sender, RoutedEventArgs e)
    { if (LeftFolders.SelectedItem is FolderNode node) { if (node.IsDirectory) ToggleFolder(node); else await OpenNode(node); } }
    private async Task OpenNode(FolderNode node)
    {
        if (_busy || node.File is null || _folderResult is null || _rootRequest is null) return;
        if (!await CanLeaveEdits()) return;
        ClearEditors();
        _detailCancellation?.Cancel();
        var cancellation = new CancellationTokenSource(); _detailCancellation = cancellation;
        var token = cancellation.Token;
        var root = _folderResult; var request = _rootRequest;
        _currentNode = node; _textFile = null; _fileResult = null; _presentation = null; _presentationVersion++;
        LeftCode.SetRows([]); RightCode.SetRows([]); Overview.SetPresentation(null);
        HexHost.Visibility = Visibility.Collapsed; PictureHost.Visibility = Visibility.Collapsed;
        TextTab.Content = "≡ " + node.Name; TextTab.IsEnabled = true; SetView(true);
        TextEmptyMessage.Text = "加载文件并对齐差异…"; TextEmptyState.Visibility = Visibility.Visible;
        string left = Path.Combine(root.LeftPath, node.RelativePath), right = Path.Combine(root.RightPath, node.RelativePath);
        SetTextHeaders(left, right, node.File);
        CancelButton.IsEnabled = true; ActivityLabel.Text = "加载文件…";
        string? empty = null;
        try
        {
            ComparisonResult result;
            if (node.File.Status == FileStatus.Error) result = new(left, right, "files", request.AlgorithmId, [node.File], []);
            else
            {
                if (node.File.Status is FileStatus.LeftOnly or FileStatus.RightOnly) empty = Path.GetTempFileName();
                var fileRequest = request with
                {
                    LeftPath = node.File.Status == FileStatus.RightOnly ? empty! : left,
                    RightPath = node.File.Status == FileStatus.LeftOnly ? empty! : right
                };
                result = await Task.Run(() => _engine.CompareAsync(fileRequest, "files", cancellationToken: token), token);
                var file = result.Files.Single() with { RelativePath = node.RelativePath };
                if (node.File.Status is FileStatus.LeftOnly or FileStatus.RightOnly) file = file with { Status = node.File.Status };
                result = result with { LeftPath = left, RightPath = right, Files = [file] };
            }
            token.ThrowIfCancellationRequested();
            _fileResult = result; _textFile = result.Files.Single();
            SetTextHeaders(left, right, _textFile);
            await RebuildPresentation(token);
            await AttachEditors(left, right, token);
            LeftFolders.SelectedItem = RightFolders.SelectedItem = node;
            ExportButton.IsEnabled = true; UpdateSummary(); LeftCode.Focus();
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_detailCancellation, cancellation)) TextEmptyMessage.Text = "已取消加载。可以返回目录选择其他文件。";
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) { TextEmptyMessage.Text = "无法加载文件：" + ex.Message; StatusLabel.Text = TextEmptyMessage.Text; }
        }
        finally
        {
            if (empty is not null) File.Delete(empty);
            if (ReferenceEquals(_detailCancellation, cancellation)) { _detailCancellation = null; CancelButton.IsEnabled = false; ActivityLabel.Text = "就绪"; }
            cancellation.Dispose();
        }
    }
    private void SetTextHeaders(string left, string right, FileComparison file)
    {
        LeftFileTitle.Text = left; LeftFileTitle.ToolTip = left; RightFileTitle.Text = right; RightFileTitle.ToolTip = right;
        LeftFileInfo.Text = file.Status == FileStatus.RightOnly ? "此侧不存在 · 占位对齐" : $"{file.Lines.Count(l => l.LeftLine is not null):N0} 行 · 左侧";
        RightFileInfo.Text = file.Status == FileStatus.LeftOnly ? "此侧不存在 · 占位对齐" : $"{file.Lines.Count(l => l.RightLine is not null):N0} 行 · 右侧";
        TextTab.Content = "≡ " + Path.GetFileName(file.RelativePath);
    }
    private static readonly HashSet<string> PictureExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    private async Task RebuildPresentation(CancellationToken token = default, bool preservePosition = false)
    {
        var file = _textFile; if (file is null) return;
        if (file.IsBinary)
        {
            if (PictureExtensions.Contains(Path.GetExtension(_fileResult?.LeftPath ?? ""))) await ShowPictureAsync(file);
            else await ShowHexAsync(token);
            return;
        }
        HexHost.Visibility = Visibility.Collapsed; PictureHost.Visibility = Visibility.Collapsed;
        if (PresentationBox.SelectedItem is not ITextPresentation presenter) throw new InvalidOperationException("没有文本展示插件。");
        int version = ++_presentationVersion;
        int context = TextFilter.SelectedIndex switch { 1 => 0, 2 => 3, _ => -1 };
        var presentation = await Task.Run(() => presenter.Build(file.Lines, context, token), token);
        token.ThrowIfCancellationRequested();
        if (version != _presentationVersion || !ReferenceEquals(file, _textFile)) return;
        _presentation = presentation; _currentHunk = -1;
        _syncText = true; LeftCode.SetRows(presentation.Rows, !preservePosition); RightCode.SetRows(presentation.Rows, !preservePosition); _syncText = false;
        Overview.SetPresentation(presentation); UpdateOverview();
        HunkLabel.Text = $"{presentation.Hunks.Count} 个差异块";
        UpdateEditState(); UpdateFileStats();
        TextEmptyState.Visibility = presentation.Rows.Count == 0 && _leftBuffer is null ? Visibility.Visible : Visibility.Collapsed;
        TextEmptyMessage.Text = file.Status == FileStatus.Error ? file.Note ?? "文件读取错误。"
            : file.IsBinary ? "此文件使用字节比较，不生成文本视图。\n" + file.Note
            : "空文件：没有可显示的文本行。";
        DetailLabel.Text = (file.IsApproximate ? "粗粒度对齐 · " : "") + (file.Note ?? "F7 / Shift+F7 跳转差异块 · Ctrl+F 查找 · 拖动选择行，Ctrl+C 复制");
    }
    /// <summary>Binary files render as a virtualized side-by-side hex dump with the differing
    /// bytes colored; the view loads at most 16&nbsp;MB per side.</summary>
    private async Task ShowHexAsync(CancellationToken token)
    {
        var file = _textFile!;
        var result = _fileResult!;
        const long limit = 16L << 20;
        try
        {
            Task<byte[]> Read(string path) => Task.Run(async () =>
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var data = new byte[Math.Min(stream.Length, limit)];
                await stream.ReadExactlyAsync(data, token);
                return data;
            }, token);
            byte[] left = await Read(result.LeftPath), right = await Read(result.RightPath);
            token.ThrowIfCancellationRequested();
            Hex.SetContent(left, right, file.BinaryDiffs!);
            HexHost.Visibility = Visibility.Visible;
            if (file.BinaryDiffs!.Count > 0)
                Hex.SetVerticalOffset(Math.Max(0, file.BinaryDiffs[0].Offset / 16 * Hex.RowHeight - 120));
            HunkLabel.Text = $"{file.BinaryDiffs!.Count} 段字节差异";
            StatusLabel.Text = $"十六进制比较 · 每侧最多加载 {limit >> 20} MB";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            HexHost.Visibility = Visibility.Collapsed;
            StatusLabel.Text = "十六进制视图加载失败：" + ex.Message;
        }
    }

    /// <summary>Pictures render side by side; when dimensions match, differing pixels are
    /// overlaid in translucent red on the right image (up to 4096² pixels).</summary>
    private async Task ShowPictureAsync(FileComparison file)
    {
        var result = _fileResult!;
        try
        {
            var left = LoadPicture(result.LeftPath);
            var right = LoadPicture(result.RightPath);
            LeftPicture.Source = left;
            RightPicture.Source = right;
            if (file.Status != FileStatus.Equal && left.PixelWidth == right.PixelWidth && left.PixelHeight == right.PixelHeight
                && (long)left.PixelWidth * left.PixelHeight <= 4096L * 4096L)
            {
                WriteableBitmap overlay = await Task.Run(() => OverlayDifference(left, right));
                RightPicture.Source = overlay;
            }
            HexHost.Visibility = Visibility.Collapsed;
            PictureHost.Visibility = Visibility.Visible;
            HunkLabel.Text = file.Note ?? "";
            StatusLabel.Text = "图片比较 · 右侧红色半透明为差异像素";
        }
        catch (Exception ex)
        {
            PictureHost.Visibility = Visibility.Collapsed;
            StatusLabel.Text = "图片视图加载失败：" + ex.Message;
        }
    }

    private static BitmapImage LoadPicture(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static WriteableBitmap OverlayDifference(BitmapSource left, BitmapSource right)
    {
        int width = left.PixelWidth, height = left.PixelHeight, stride = width * 4;
        var a = new byte[stride * height];
        var b = new byte[stride * height];
        left.CopyPixels(a, stride, 0);
        right.CopyPixels(b, stride, 0);
        var pixels = new int[width * height];
        right.CopyPixels(pixels, stride, 0);
        int changed = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            int o = i * 4;
            if (a[o] == b[o] && a[o + 1] == b[o + 1] && a[o + 2] == b[o + 2]) continue;
            pixels[i] = unchecked((int)0x46FF2020);   // translucent red over the right image
            changed++;
        }
        var overlay = new WriteableBitmap(right);
        overlay.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
        overlay.Freeze();
        return overlay;
    }

    private async void TextFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _textFile is null) return;
        try { await RebuildPresentation(); } catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }
    private void CodeScrolled(DiffPane source)
    {
        if (_syncText) return;
        var other = ReferenceEquals(source, LeftCode) ? RightCode : LeftCode;
        _syncText = true;
        other.SetVerticalOffset(source.VerticalOffset);
        if (LockHorizontal.IsChecked == true) other.SetHorizontalOffset(source.HorizontalOffset);
        _syncText = false; UpdateOverview();
    }
    private void UpdateOverview()
    {
        if (_presentation is null || _presentation.Rows.Count == 0) return;
        int first = Math.Clamp(LeftCode.FirstVisibleRow, 0, _presentation.Rows.Count - 1);
        int last = Math.Clamp(LeftCode.LastVisibleRow, 0, _presentation.Rows.Count - 1);
        Overview.SetViewport(_presentation.Rows[first].SourceIndex, _presentation.Rows[last].SourceIndex);
    }
    private void CodeSelected(int index, DiffPane? source = null)
    {
        if (!ReferenceEquals(source, LeftCode)) LeftCode.SelectRow(index, false);
        if (!ReferenceEquals(source, RightCode)) RightCode.SelectRow(index, false);
        if (_presentation is not null && index >= 0 && index < _presentation.Rows.Count)
        {
            _currentHunk = _presentation.Rows[index].HunkId;
            HunkLabel.Text = _currentHunk >= 0 ? $"差异 {_currentHunk + 1} / {_presentation.Hunks.Count}" : $"{_presentation.Hunks.Count} 个差异块";
        }
        else _currentHunk = -1;
        UpdateEditState();
    }
    private void NavigateSource(int source)
    {
        if (_presentation is null || _presentation.Rows.Count == 0) return;
        int row = 0;
        for (int i = 0; i < _presentation.Rows.Count; i++) { if (_presentation.Rows[i].SourceIndex > source) break; row = i; }
        LeftCode.SelectRow(row); RightCode.SelectRow(row); CodeSelected(row); LeftCode.Focus();
    }
    private void PreviousDiff(object sender, RoutedEventArgs e) => NavigateHunk(-1);
    private void NextDiff(object sender, RoutedEventArgs e) => NavigateHunk(1);
    private void NavigateHunk(int direction)
    {
        if (FolderView.Visibility == Visibility.Visible)
        {
            var changed = _folderRows.Where(n => !n.IsDirectory && n.Status != FileStatus.Equal).ToArray();
            if (changed.Length == 0) return;
            int index = Array.IndexOf(changed, LeftFolders.SelectedItem as FolderNode);
            if (index < 0) index = direction > 0 ? -1 : 0;
            var node = changed[(index + direction + changed.Length) % changed.Length];
            LeftFolders.SelectedItem = node; LeftFolders.ScrollIntoView(node); RightFolders.ScrollIntoView(node); return;
        }
        if (_presentation is null || _presentation.Hunks.Count == 0) return;
        int count = _presentation.Hunks.Count;
        int target = _currentHunk < 0 ? direction > 0 ? 0 : count - 1 : (_currentHunk + direction + count) % count;
        NavigateSource(_presentation.Hunks[target].StartRow);
    }
    private async void PreviousFile(object sender, RoutedEventArgs e) => await NavigateFile(-1);
    private async void NextFile(object sender, RoutedEventArgs e) => await NavigateFile(1);
    private async Task NavigateFile(int direction)
    {
        var files = _folderRows.Where(n => !n.IsDirectory).ToArray(); if (files.Length == 0) return;
        int index = Array.IndexOf(files, _currentNode);
        if (index < 0) index = direction > 0 ? -1 : 0;
        var node = files[(index + direction + files.Length) % files.Length];
        await OpenNode(node);
    }
    private void ShowEolClick(object sender, RoutedEventArgs e)
    { LeftCode.ShowEndings = RightCode.ShowEndings = ShowEol.IsChecked == true; LeftCode.InvalidateVisual(); RightCode.InvalidateVisual(); }
    private void WrapChanged(object sender, RoutedEventArgs e)
    {
        bool wrap = WrapToggle.IsChecked == true;
        LeftCode.WrapLines = RightCode.WrapLines = wrap;
    }
    private void ZoomOut(object sender, RoutedEventArgs e) => Zoom(-1);
    private void ZoomIn(object sender, RoutedEventArgs e) => Zoom(1);
    private void Zoom(int delta) { _codeFontSize = Math.Clamp(_codeFontSize + delta, 10, 24); _syncText = true; LeftCode.Zoom(_codeFontSize); RightCode.Zoom(_codeFontSize); _syncText = false; UpdateOverview(); }
    private async void FindNextClick(object sender, RoutedEventArgs e) => await FindNext();
    private async void FindKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await FindNext(); } }
    private async Task FindNext()
    {
        if (_textFile is null || FindBox.Text.Length == 0) return;
        if (TextFilter.SelectedIndex != 0) { _ready = false; TextFilter.SelectedIndex = 0; _ready = true; await RebuildPresentation(); }
        if (_presentation is null || _presentation.Rows.Count == 0) return;
        int count = _presentation.Rows.Count, selected = LeftCode.SelectedRow;
        for (int step = 1; step <= count; step++)
        {
            int index = (selected + step + count) % count; var row = _presentation.Rows[index];
            if (row.Left?.Text.Contains(FindBox.Text, StringComparison.OrdinalIgnoreCase) == true || row.Right?.Text.Contains(FindBox.Text, StringComparison.OrdinalIgnoreCase) == true)
            { LeftCode.SelectRow(index); RightCode.SelectRow(index); CodeSelected(index); StatusLabel.Text = $"找到：{FindBox.Text}"; return; }
        }
        StatusLabel.Text = $"未找到：{FindBox.Text}";
    }

    private void ReplaceToggleChanged(object sender, RoutedEventArgs e)
    {
        bool show = ReplaceToggle.IsChecked == true;
        ReplaceBox.Visibility = ReplaceOneButton.Visibility = ReplaceAllButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) { ReplaceBox.Focus(); ReplaceBox.SelectAll(); }
    }
    private async void ReplaceKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await ReplaceNext(); }
        else if (e.Key == Key.Escape) { e.Handled = true; ReplaceToggle.IsChecked = false; }
    }
    private async void ReplaceOneClick(object sender, RoutedEventArgs e) => await ReplaceNext();
    private async Task ReplaceNext()
    {
        var pane = _activePane ?? LeftCode;
        if (pane.Buffer is null) { StatusLabel.Text = "此文件为只读。"; return; }
        if (FindBox.Text.Length == 0) { StatusLabel.Text = "请输入要查找的内容。"; return; }
        string text = pane.Buffer.Text;
        int from = Math.Min(Math.Max(pane.CaretOffset, 0), text.Length);
        int index = text.IndexOf(FindBox.Text, from, StringComparison.OrdinalIgnoreCase);
        if (index < 0) index = text.IndexOf(FindBox.Text, StringComparison.OrdinalIgnoreCase);
        if (index < 0) { StatusLabel.Text = $"未找到：{FindBox.Text}"; return; }
        pane.Focus();
        pane.SetCaret(index, false, false);
        pane.SetCaret(index + FindBox.Text.Length, true, false);
        pane.ReplaceSelection(ReplaceBox.Text, "Replace");
        await _editTask;
        StatusLabel.Text = $"已替换 1 处 · {FindBox.Text} → {ReplaceBox.Text}";
    }
    private async void ReplaceAllClick(object sender, RoutedEventArgs e)
    {
        var pane = _activePane ?? LeftCode;
        if (pane.Buffer is null) { StatusLabel.Text = "此文件为只读。"; return; }
        if (FindBox.Text.Length == 0) { StatusLabel.Text = "请输入要查找的内容。"; return; }
        string text = pane.Buffer.Text;
        var builder = new StringBuilder(text.Length);
        int at = 0, count = 0;
        while (true)
        {
            int found = text.IndexOf(FindBox.Text, at, StringComparison.OrdinalIgnoreCase);
            if (found < 0) { builder.Append(text.AsSpan(at)); break; }
            builder.Append(text.AsSpan(at, found - at)).Append(ReplaceBox.Text);
            at = found + FindBox.Text.Length;
            count++;
        }
        if (count == 0) { StatusLabel.Text = $"未找到：{FindBox.Text}"; return; }
        pane.Buffer.Apply(new Contracts.TextEdit(0, text.Length, builder.ToString(), $"全部替换 ×{count}"));
        AfterProgrammaticEdit(pane, $"已替换 {count} 处 · {FindBox.Text} → {ReplaceBox.Text}");
        await _editTask;
    }
    private async void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (await EditingShortcut(e)) return;
        if (await EditorOpsShortcut(e)) return;
        if (e.Key == Key.F5)
        {
            e.Handled = true;
            if (TextView.Visibility == Visibility.Visible && _folderResult is not null && _currentNode is not null) await OpenNode(_currentNode);
            else await CompareAsync();
        }
        else if (e.Key == Key.F7) { e.Handled = true; NavigateHunk(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1); }
        else if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        { e.Handled = true; if (TextView.Visibility == Visibility.Visible) { FindBox.Focus(); FindBox.SelectAll(); } else { FolderSearch.Focus(); FolderSearch.SelectAll(); } }
        else if (e.Key == Key.F3) { e.Handled = true; if (TextView.Visibility == Visibility.Visible) await FindNext(); }
        else if (e.Key == Key.H && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        { e.Handled = true; ReplaceToggle.IsChecked = true; ReplaceBox.Focus(); ReplaceBox.SelectAll(); }
        else if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        { e.Handled = true; WrapToggle.IsChecked = WrapToggle.IsChecked != true; }
        else if (e.Key == Key.N && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; NewWindowClick(sender, e); }
        else if (e.Key == Key.Escape) { SettingsToggle.IsChecked = false; CancelClick(sender, e); }
        else if (e.SystemKey == Key.Left && _folderResult is not null) { SetView(false); e.Handled = true; }
        else if (e.SystemKey is Key.Up or Key.Down && TextView.Visibility == Visibility.Visible) { e.Handled = true; await NavigateFile(e.SystemKey == Key.Up ? -1 : 1); }
    }
    private void WindowDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        if (paths.Length > 1) { LeftPath.Text = paths[0]; RightPath.Text = paths[1]; }
        else if (e.GetPosition(this).X < ActualWidth / 2) LeftPath.Text = paths[0]; else RightPath.Text = paths[0];
        e.Handled = true;
    }
    private async void ExportClick(object sender, RoutedEventArgs e)
    {
        var result = TextView.Visibility == Visibility.Visible ? _fileResult : _folderResult;
        if (result is null || FormatBox.SelectedItem is not IResultFormatter formatter) return;
        var dialog = new SaveFileDialog { Filter = $"{formatter.DisplayName}|*{formatter.FileExtension}", DefaultExt = formatter.FileExtension, FileName = "comparison" + formatter.FileExtension };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            string output = Path.GetFullPath(dialog.FileName);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (string input in new[] { result.LeftPath, result.RightPath, _rootRequest?.LeftPath, _rootRequest?.RightPath }.OfType<string>())
                if (output.Equals(input, comparison) || (Directory.Exists(input) && output.StartsWith(Path.TrimEndingDirectorySeparator(input) + Path.DirectorySeparatorChar, comparison)))
                    throw new InvalidOperationException("请将报告保存到被比较的文件或目录之外。");
            await using var writer = new StreamWriter(output, false, new UTF8Encoding(false));
            await formatter.WriteAsync(result, writer); StatusLabel.Text = "报告已保存：" + output;
        }
        catch (Exception ex) { StatusLabel.Text = "导出失败：" + ex.Message; }
    }
    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); if (child is T typed) return typed; var found = FindChild<T>(child); if (found is not null) return found; }
        return null;
    }
    private static T? FindParent<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null) { if (element is T typed) return typed; element = element is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(element); }
        return null;
    }
    private async void SmokeTest(object sender, RoutedEventArgs e)
    {
        int exit = 0;
        try
        {
            if (_plugins.Diagnostics.Count != 0) throw new InvalidOperationException(string.Join("; ", _plugins.Diagnostics));
            await CompareAsync();
            if (_folderResult is null && _fileResult is null) throw new InvalidOperationException("No comparison result: " + StatusLabel.Text);
            string? screenshot = Arg("--screenshot");
            if (_folderResult is not null)
            {
                if (_folderResult.HasErrors || !ReferenceEquals(LeftFolders.ItemsSource, RightFolders.ItemsSource)) throw new InvalidOperationException("Folder panes are not aligned.");
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (screenshot is not null) SaveScreenshot(screenshot);
                var folder = _folderRows.FirstOrDefault(n => n.IsDirectory);
                if (folder is not null)
                {
                    int expanded = _folderRows.Count; ToggleFolder(folder);
                    if (_folderRows.Count >= expanded) throw new InvalidOperationException("Collapse did not hide descendants.");
                    ToggleFolder(folder);
                }
                FolderFilter.SelectedIndex = 1;
                if (_folderRows.Any(n => !n.IsDirectory && n.Status == FileStatus.Equal)) throw new InvalidOperationException("Difference filter failed.");
                FolderFilter.SelectedIndex = 0;
                var node = _folderRows.FirstOrDefault(n => !n.IsDirectory && n.Status == FileStatus.Modified && n.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) ?? _folderRows.First(n => !n.IsDirectory);
                LeftFolders.SelectedItem = node;
                await OpenNode(node);
                if (_textFile is null) throw new InvalidOperationException("Opening directory file failed.");
                SetView(false);
                if (!ReferenceEquals(LeftFolders.SelectedItem, node) || !ReferenceEquals(RightFolders.SelectedItem, node)) throw new InvalidOperationException("Folder selection was lost.");
                SetView(true);
                if (screenshot is not null) screenshot = Path.Combine(Path.GetDirectoryName(screenshot)!, Path.GetFileNameWithoutExtension(screenshot) + ".text.png");
            }
            if (_presentation is null || !ReferenceEquals(LeftCode.Rows, RightCode.Rows)) throw new InvalidOperationException("Text panes do not share aligned rows.");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            LeftCode.SetVerticalOffset(LeftCode.RowHeight * 5);
            if (Math.Abs(LeftCode.VerticalOffset - RightCode.VerticalOffset) > .1) throw new InvalidOperationException("Vertical scrolling is not synchronized.");
            LeftCode.SetVerticalOffset(0);
            if (_presentation.Hunks.Count > 0)
            {
                NavigateHunk(1);
                if (_currentHunk != 0) throw new InvalidOperationException("First-hunk navigation failed.");
                int last = _presentation.Hunks.Count - 1;
                NavigateHunk(-1);
                if (_currentHunk != last) throw new InvalidOperationException("Previous-hunk wrap failed.");
            }
            _ready = false; TextFilter.SelectedIndex = 2; _ready = true; await RebuildPresentation();
            if (LeftCode.Rows.Any(r => !r.IsDifferent && r.HiddenLines == 0 && r.Left is null && r.Right is null)) throw new InvalidOperationException("Invalid context row.");
            _ready = false; TextFilter.SelectedIndex = 0; _ready = true; await RebuildPresentation();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (screenshot is not null) SaveScreenshot(screenshot);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-result.json"), System.Text.Json.JsonSerializer.Serialize(new { success = true, folderRows = _folderRows.Count, alignedRows = _presentation?.Rows.Count, hunks = _presentation?.Hunks.Count }));
        }
        catch (Exception ex) { exit = 2; File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-error.txt"), ex.ToString()); }
        Application.Current.Shutdown(exit);
    }
    private void SaveScreenshot(string path)
    {
        UpdateLayout(); var surface = RootLayout;
        var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen()) dc.DrawRectangle(new VisualBrush(surface), null, new Rect(0, 0, surface.ActualWidth, surface.ActualHeight));
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
}
