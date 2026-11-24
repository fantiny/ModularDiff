
using System.Collections.Concurrent;
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
    private string _committedLeft = "", _committedRight = "";
    private readonly ComparisonEngine _engine;
    private readonly string[] _arguments;
    private ComparisonResult? _folderResult, _fileResult;
    private ComparisonRequest? _rootRequest;
    private FolderTree? _tree;
    private readonly List<FolderNode> _folderRows = [];
    private FolderNode? _currentNode;
    /// <summary>Sticky left/right file picks for BC-style cross-name compare. Survives the
    /// non-Ctrl first click of a double-click so Open still uses the independent pair.</summary>
    // 目录视图选择:两侧 FolderList 各自独立;跨文件配对在打开时解析(见 OpenSelectedFiles)。
    private FileComparison? _textFile;
    private CancellationTokenSource? _comparisonCancellation, _detailCancellation;
    private bool _ready, _busy, _syncText, _syncFolderScroll, _syncFolderSelection;
    private int _currentHunk = -1;
    private int _folderSnapshotVersion;
    private readonly ConcurrentQueue<FileComparison> _folderAdds = new();
    private readonly ConcurrentQueue<FileComparison> _folderUpdates = new();
    private string _folderProgressStatus = "";
    private string _folderProgressActivity = "";
    private volatile bool _folderUiDirty;
    private DispatcherTimer? _folderUiTimer;
    private double _codeFontSize = 14;
    private static int _windowCount;
    private sealed record ModuleChoice(string? Id, string DisplayName);

    /// <summary>Progress that never marshals to the UI SynchronizationContext — scan/compare
    /// callbacks stay on the thread pool so the dispatcher queue is not flooded.</summary>
    private sealed class ThreadPoolProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => ThreadPool.UnsafeQueueUserWorkItem(static state =>
        {
            var (action, item) = ((Action<T>, T))state!;
            try { action(item); }
            catch { /* progress must not tear down the compare */ }
        }, (handler, value));
    }

    public MainWindow(string[] arguments)
    {
        _arguments = arguments;
        InitializeComponent();
        int cascade = Interlocked.Increment(ref _windowCount) - 1;   // 新窗口级联偏移，避免完全叠加难发现
        Left = 70 + (cascade % 12) * 36; Top = 46 + (cascade % 12) * 30;
        _plugins = App.Plugins;
        Controls.Lang.Changed += LocalizeUI;   // shared catalog; every later switch re-applies the whole window
        Closed += (_, _) => { Controls.Lang.Changed -= LocalizeUI; _comparisonCancellation?.Cancel(); _detailCancellation?.Cancel(); };
        ReportDeclarativeFileIssues();
        _engine = new(_plugins);
        _session = new(_plugins);
        LangBox.ItemsSource = ModularDiff.Gui.Controls.Lang.Available;
        LangBox.SelectedItem = ModularDiff.Gui.Controls.Lang.CurrentName;
        AlgorithmBox.ItemsSource = _plugins.GetAll<IDiffAlgorithm>();
        AlgorithmBox.SelectedItem = _plugins.GetAll<IDiffAlgorithm>().FirstOrDefault(p => p.Id == "myers");
        ModuleBox.ItemsSource = new[] { new ModuleChoice(null, ModularDiff.Gui.Controls.Lang.Get("module_auto")) }.Concat(_plugins.GetAll<IComparisonModule>().Select(p => new ModuleChoice(p.Id, p.DisplayName))).ToArray();
        ModuleBox.SelectedIndex = 0;
        PresentationBox.ItemsSource = _plugins.GetAll<ITextPresentation>();
        PresentationBox.SelectedItem = _plugins.GetAll<ITextPresentation>().FirstOrDefault(p => p.Id == "side-by-side") ?? _plugins.GetAll<ITextPresentation>().FirstOrDefault();
        FormatBox.ItemsSource = _plugins.GetAll<IResultFormatter>(); FormatBox.SelectedIndex = 0;
        LeftPath.Text = Arg("--left") ?? ""; RightPath.Text = Arg("--right") ?? "";
        PluginSummary.Text = ModularDiff.Gui.Controls.Lang.Get("plugins_loaded", _plugins.GetAll<IPlugin>().Count);
        LeftCode.ViewChanged += CodeScrolled; RightCode.ViewChanged += CodeScrolled;
        LeftCode.RowSelected += index => CodeSelected(index, LeftCode);
        RightCode.RowSelected += index => CodeSelected(index, RightCode);
        LeftCode.ExpandContext += () => TextFilter.SelectedIndex = 0;
        RightCode.ExpandContext += () => TextFilter.SelectedIndex = 0;
        Overview.NavigateRequested += NavigateSource;
        LeftCode.ShowEndings = RightCode.ShowEndings = false;
        if (_plugins.Diagnostics.Count > 0)
        {
            StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_plugins_failed", string.Join(" | ", _plugins.Diagnostics));
            CompareButton.IsEnabled = false;
        }
        LeftPath.TextChanged += (_, _) => UpdatePathBoxTips();
        RightPath.TextChanged += (_, _) => UpdatePathBoxTips();
        InitializeEditing(); InitializeSync(); InitializeLineTools(); InitializeSessions(); InitializeFolderOps(); InitializeTabs();
        LeftFolders.SelectionChanged += FolderSelectionChanged; RightFolders.SelectionChanged += FolderSelectionChanged;
        _ready = true;
        LocalizeUI();   // plugin lists and state-derived labels exist only after initialization
        if (double.TryParse(Arg("--smoke-width"), out double width)) Width = Math.Max(MinWidth, width);
        if (arguments.Contains("--filter-smoke-test")) Loaded += FilterSmokeTest;
        else if (arguments.Contains("--sync-smoke-test")) Loaded += SyncSmokeTest;
        else if (arguments.Contains("--edit-smoke-test")) Loaded += EditSmokeTest;
        else if (arguments.Contains("--smoke-test")) Loaded += SmokeTest;
        else if (arguments.Contains("--auto-compare")) Loaded += async (_, _) =>
        {
            try { await CompareAndTrackSession(); }
            catch (Exception ex) { System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "md-crash.log"), ex.ToString()); StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_auto_compare_failed", ModularDiff.Gui.Controls.Lang.Text(ex.Message)); }
        };
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
            catch (ArgumentException ex) { throw new InvalidOperationException(ModularDiff.Gui.Controls.Lang.Get("err_unimportant_rule", pattern, ModularDiff.Gui.Controls.Lang.Text(ex.Message))); }
        }
        return patterns.Length > 0 ? patterns : null;
    }
    private void BrowseFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = ModularDiff.Gui.Controls.Lang.Get("browse_compare_file") };
        if (dialog.ShowDialog(this) == true) Target(sender).Text = dialog.FileName;
    }
    private void BrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = ModularDiff.Gui.Controls.Lang.Get("browse_compare_folder") };
        if (dialog.ShowDialog(this) == true) Target(sender).Text = dialog.FolderName;
    }
    private TextBox Target(object sender) => (string)((Button)sender).Tag == "left" ? LeftPath : RightPath;
    // The Compare button and Enter share one entry: validate, then re-compare (CommitPathBarAsync).
    private async void CompareClick(object sender, RoutedEventArgs e) => await CommitPathBarAsync();

    private async void PathBarKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await CommitPathBarAsync();
    }

    /// <summary>Enter in the path bar or the Compare button: validates both paths, then re-compares.
    /// Two files entered while a folder comparison is loaded open as a text tab, so the Folder tab
    /// keeps its comparison; anything else replaces the root comparison. An invalid path restores the
    /// last committed pair — the boxes always describe what is on screen, which sync and session
    /// bookkeeping rely on — and reports the reason at the box itself; with nothing committed yet
    /// (fresh window) the typed text is kept so it can be corrected.</summary>
    private async Task CommitPathBarAsync()
    {
        if (_busy) return;
        if (!PathBarCommit.TryResolve(LeftPath.Text, RightPath.Text, out string left, out string right, out string? error))
        {
            string reason = error is null ? ModularDiff.Gui.Controls.Lang.Get("status_path_restored") : ModularDiff.Gui.Controls.Lang.Text(error);
            TextBox atFault = PathBoxAtFault(LeftPath.Text, RightPath.Text);
            if (_committedLeft.Length > 0 || _committedRight.Length > 0) { LeftPath.Text = _committedLeft; RightPath.Text = _committedRight; }
            ShowPathError(atFault, reason);
            return;
        }
        if (_folderResult is not null && _rootRequest is not null && File.Exists(left) && File.Exists(right))
        {
            LeftPath.Text = left; RightPath.Text = right;
            if (await OpenPathBarPair(left, right)) RememberCommittedPaths(left, right);
            else { LeftPath.Text = _committedLeft; RightPath.Text = _committedRight; }
            return;
        }
        if (!await CanLeaveEdits())
        {
            LeftPath.Text = _committedLeft;
            RightPath.Text = _committedRight;
            return;
        }
        LeftPath.Text = left;
        RightPath.Text = right;
        await CompareAndTrackSession();
        if (_rootRequest is not null
            && string.Equals(_rootRequest.LeftPath, left, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_rootRequest.RightPath, right, StringComparison.OrdinalIgnoreCase))
            RememberCommittedPaths(left, right);
        else
        {
            LeftPath.Text = _committedLeft;
            RightPath.Text = _committedRight;
        }
    }

    /// <summary>Which path box to flag after a rejected commit: the first side that does not resolve
    /// to an existing file or folder, or the right box when both exist (mixed kinds).</summary>
    private TextBox PathBoxAtFault(string leftText, string rightText)
    {
        static bool Exists(string? text)
        {
            try { string full = Path.GetFullPath((text ?? "").Trim()); return File.Exists(full) || Directory.Exists(full); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        }
        return Exists(leftText) ? RightPath : LeftPath;
    }

    /// <summary>Reports a rejected path where it happened: status bar plus a tooltip that opens at
    /// once on the offending box (a status line at the bottom of the window is easy to miss).</summary>
    private void ShowPathError(TextBox box, string reason)
    {
        StatusLabel.Text = reason;
        var hint = new ToolTip { Content = reason, PlacementTarget = box, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, StaysOpen = true };
        box.ToolTip = hint;
        box.Focus(); box.SelectAll();
        hint.IsOpen = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            hint.IsOpen = false;
            if (ReferenceEquals(box.ToolTip, hint)) UpdatePathBoxTips();   // back to the regular full-path hint
        };
        timer.Start();
    }

    /// <summary>Path box tooltips lead with the full path (the boxes clip long paths), then the hint.</summary>
    private void UpdatePathBoxTips()
    {
        if (LeftPath is null || RightPath is null) return;
        foreach (var box in new[] { LeftPath, RightPath })
        {
            if (box.ToolTip is ToolTip { IsOpen: true }) continue;   // an error hint is showing
            string hint = ModularDiff.Gui.Controls.Lang.Get("path_tip");
            box.ToolTip = string.IsNullOrWhiteSpace(box.Text) ? hint : box.Text.Trim() + "\n" + hint;
        }
    }

    /// <summary>Opens two files typed into the path bar as a text tab while the folder comparison
    /// stays loaded. Returns false when the user cancelled or the load failed.</summary>
    private async Task<bool> OpenPathBarPair(string left, string right)
    {
        string name = Path.GetFileName(left), other = Path.GetFileName(right);
        string caption = string.Equals(Path.GetDirectoryName(left), Path.GetDirectoryName(right), StringComparison.OrdinalIgnoreCase)
            ? name : name + " ↔ " + other;
        if (!await LoadFilePairAsync(left, right, caption, knownError: null, sourceNode: null)) return false;
        if (_rootRequest is { } request) RecordRecentComparison(request with { LeftPath = left, RightPath = right });
        return true;
    }

    private void RememberCommittedPaths(string left, string right)
    {
        _committedLeft = left;
        _committedRight = right;
    }
    private async Task CompareAsync()
    {
        if (_busy || !await CanLeaveEdits()) return;
        ClearEditors();
        _detailCancellation?.Cancel();
        _comparisonCancellation?.Dispose(); _comparisonCancellation = new();
        var token = _comparisonCancellation.Token;
        _busy = true; CompareButton.IsEnabled = false; CancelButton.IsEnabled = true; ExportButton.IsEnabled = false;
        _binarySession = null;   // the panes are about to show a new root comparison, not a tab's bytes
        _folderResult = _fileResult = null; _rootRequest = null; _tree = null; _currentNode = null; _textFile = null; _presentation = null; _folderRows.Clear();
        _folderSnapshotVersion++;
        while (_folderAdds.TryDequeue(out _)) { }
        while (_folderUpdates.TryDequeue(out _)) { }
        _folderProgressStatus = ""; _folderProgressActivity = ModularDiff.Gui.Controls.Lang.Get("activity_comparing"); _folderUiDirty = false;
        LeftFolders.SetRows([]); RightFolders.SetRows([]);
        LeftCode.SetRows([]); RightCode.SetRows([]); Overview.SetPresentation(null);
        HexHost.Visibility = Visibility.Collapsed; PictureHost.Visibility = Visibility.Collapsed;
        FolderView.Visibility = Visibility.Visible; TextView.Visibility = Visibility.Collapsed;
        ActivityLabel.Text = ModularDiff.Gui.Controls.Lang.Get("activity_comparing");
        StartFolderUiTimer();
        try
        {
            if (string.IsNullOrWhiteSpace(LeftPath.Text) || string.IsNullOrWhiteSpace(RightPath.Text)) throw new InvalidOperationException(ModularDiff.Gui.Controls.Lang.Get("status_fill_paths"));
            if (AlgorithmBox.SelectedItem is not IDiffAlgorithm algorithm) throw new InvalidOperationException(ModularDiff.Gui.Controls.Lang.Get("status_no_algorithm"));
            var request = new ComparisonRequest(Path.GetFullPath(LeftPath.Text), Path.GetFullPath(RightPath.Text), algorithm.Id,
                new(IgnoreCaseBox.IsChecked == true, IgnoreWhitespaceBox.IsChecked == true, IgnoreEolBox.IsChecked == true, RecursiveBox.IsChecked == true,
                    UnimportantPatterns: ParseUnimportantPatterns()));
            string? module = (ModuleBox.SelectedItem as ModuleChoice)?.Id;
            // Thread-pool progress: never Post to the UI sync context (that was freezing the UI).
            IProgress<ComparisonProgress> progress = new ThreadPoolProgress<ComparisonProgress>(p => OnDirectoryProgress(p, token));
            var result = await Task.Run(() => _engine.CompareAsync(request, module, progress, token), token);
            _rootRequest = request;
            // Drop progressive queues — final rebuild is authoritative and avoids a UI hitch
            // flushing hundreds of thousands of deferred adds/updates.
            while (_folderAdds.TryDequeue(out _)) { }
            while (_folderUpdates.TryDequeue(out _)) { }
            if (Directory.Exists(request.LeftPath) && Directory.Exists(request.RightPath))
                await ApplyFinalFolderResult(result, token);
            else
            {
                _fileResult = result; _textFile = result.Files.Single();
                SetTextHeaders(result.LeftPath, result.RightPath, _textFile);
                await RebuildPresentation(token); await AttachEditors(result.LeftPath, result.RightPath, token);
                SetView(true); LeftCode.Focus();
            }
            ExportButton.IsEnabled = true;
            UpdateSummary();
            RecordRecentComparison(request);
            RememberCommittedPaths(request.LeftPath, request.RightPath);
            LeftPath.Text = request.LeftPath;
            RightPath.Text = request.RightPath;
        }
        catch (OperationCanceledException) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_cancelled"); }
        catch (Exception ex) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_compare_failed_msg", ModularDiff.Gui.Controls.Lang.Text(ex.Message)); }
        finally
        {
            StopFolderUiTimer();
            _busy = false; CompareButton.IsEnabled = _plugins.Diagnostics.Count == 0; CancelButton.IsEnabled = false; ActivityLabel.Text = ModularDiff.Gui.Controls.Lang.Get("activity_ready");
        }
    }

    /// <summary>Runs on a thread-pool thread. Only enqueues work — never touches WPF objects.</summary>
    private void OnDirectoryProgress(ComparisonProgress progress, CancellationToken token)
    {
        if (!_busy || token.IsCancellationRequested) return;
        if (progress.PartialResult is { } partial && Directory.Exists(partial.LeftPath))
        {
            // Root (or rare full) snapshot: build tree off the UI thread, then ask dispatcher to bind.
            int version = Interlocked.Increment(ref _folderSnapshotVersion);
            try
            {
                var tree = FolderTree.Build(partial, token);
                if (token.IsCancellationRequested || version != _folderSnapshotVersion) return;
                Dispatcher.BeginInvoke(() =>
                {
                    if (!_busy || version != _folderSnapshotVersion) return;
                    string? selectedPath = (LeftFolders.SelectedItem as FolderNode)?.RelativePath;
                    _tree = tree;
                    _folderResult = partial;
                    RefreshFolderRows(selectedPath is null ? null : tree.All.FirstOrDefault(n =>
                        string.Equals(n.RelativePath, selectedPath, StringComparison.OrdinalIgnoreCase)));
                    ExportButton.IsEnabled = true;
                    OpenButton.IsEnabled = true;
                    UpdateSummary();
                }, DispatcherPriority.Background);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { /* ignore build races */ }
        }
        if (progress.Added is { Count: > 0 } added)
        {
            foreach (var item in added) _folderAdds.Enqueue(item);
            _folderUiDirty = true;
        }
        if (progress.FileUpdate is { } update)
        {
            _folderUpdates.Enqueue(update);
            _folderUiDirty = true;
        }
        if (progress.Path.StartsWith("scan:", StringComparison.Ordinal))
        {
            _folderProgressStatus = progress.Path == "scan:root"
                ? ModularDiff.Gui.Controls.Lang.Get("folder_scan_root")
                : ModularDiff.Gui.Controls.Lang.Get("folder_scan_progress", progress.Completed.ToString("N0"));
            _folderProgressActivity = ModularDiff.Gui.Controls.Lang.Get("activity_scanning");
            _folderUiDirty = true;
        }
        else if (progress.Total > 0)
        {
            _folderProgressStatus = ModularDiff.Gui.Controls.Lang.Get("folder_compare_progress", progress.Completed, progress.Total, progress.Path);
            _folderProgressActivity = ModularDiff.Gui.Controls.Lang.Get("activity_comparing");
            _folderUiDirty = true;
        }
    }

    private void StartFolderUiTimer()
    {
        StopFolderUiTimer();
        _folderUiTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _folderUiTimer.Tick += (_, _) => FlushFolderUi(force: false);
        _folderUiTimer.Start();
    }

    private void StopFolderUiTimer()
    {
        if (_folderUiTimer is null) return;
        _folderUiTimer.Stop();
        _folderUiTimer = null;
    }

    /// <summary>UI-thread only: apply a budgeted batch of adds/updates so input stays responsive.</summary>
    private void FlushFolderUi(bool force)
    {
        if (!_folderUiDirty && !force && _folderAdds.IsEmpty && _folderUpdates.IsEmpty) return;
        if (_folderProgressStatus.Length > 0) StatusLabel.Text = _folderProgressStatus;
        if (_folderProgressActivity.Length > 0) ActivityLabel.Text = _folderProgressActivity;

        if (_tree is null || _folderResult is null)
        {
            // Root tree not bound yet — keep dirty so queued adds flush after BeginInvoke binds it.
            _folderUiDirty = !_folderAdds.IsEmpty || !_folderUpdates.IsEmpty || _folderUiDirty;
            return;
        }
        _folderUiDirty = false;

        int budget = force ? int.MaxValue : 400;
        int changed = 0;
        var batch = new List<FileComparison>();
        while (budget > 0 && _folderAdds.TryDequeue(out var add))
        {
            batch.Add(add);
            budget--;
        }
        if (batch.Count > 0)
            changed += _tree.MergeAdded(batch, _folderResult.LeftPath, _folderResult.RightPath);
        while (budget > 0 && _folderUpdates.TryDequeue(out var update))
        {
            if (_tree.ApplyFileUpdate(update, _folderResult.LeftPath, _folderResult.RightPath)) changed++;
            budget--;
        }
        if (changed > 0 || force)
        {
            RefreshFolderRows(LeftFolders.SelectedItem as FolderNode);
            UpdateSummary();
        }
        // More work pending — keep dirty so the next tick continues.
        if (!_folderAdds.IsEmpty || !_folderUpdates.IsEmpty) _folderUiDirty = true;
    }

    private async Task ApplyFinalFolderResult(ComparisonResult result, CancellationToken token)
    {
        _folderResult = result;
        var expanded = _tree?.All.Where(n => n.IsDirectory && n.IsExpanded)
            .Select(n => n.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? selectedPath = (LeftFolders.SelectedItem as FolderNode)?.RelativePath;
        var tree = await Task.Run(() => FolderTree.Build(result, token), token);
        if (expanded is not null)
            foreach (var node in tree.All.Where(n => n.IsDirectory && expanded.Contains(n.RelativePath)))
                node.IsExpanded = true;
        _tree = tree;
        RefreshFolderRows(selectedPath is null ? null : tree.All.FirstOrDefault(n =>
            string.Equals(n.RelativePath, selectedPath, StringComparison.OrdinalIgnoreCase)));
    }
    /// <summary>File facts for the status bar: line counts, encoding and line-ending style per side.
    /// Cleared on the folder tab so directory view does not keep the last file's stats/hunks.</summary>
    private void UpdateFileStats()
    {
        if (TextView.Visibility != Visibility.Visible || _leftBuffer is null || _rightBuffer is null)
        {
            FileStatsLabel.Text = "";
            return;
        }
        static string Describe(IEditBuffer buffer) =>
            ModularDiff.Gui.Controls.Lang.Get("file_stats_lines", DocumentLines.Parse(buffer.Text).Count.ToString("N0"), buffer.Snapshot().Encoding.Name, buffer.NewLine == "\r\n" ? "CRLF" : buffer.NewLine == "\r" ? "CR" : "LF");
        FileStatsLabel.Text = ModularDiff.Gui.Controls.Lang.Get("file_stats", Describe(_leftBuffer), Describe(_rightBuffer));
    }

    private void UpdateSummary()
    {
        var result = TextView.Visibility == Visibility.Visible ? _fileResult : _folderResult;
        if (result is null) return;
        int files = result.Files.Count(f => !f.IsDirectory);
        int pending = result.Files.Count(f => !f.IsDirectory && f.Status == FileStatus.Pending);
        StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_summary", files,
                result.Files.Count(f => !f.IsDirectory && f.Status == FileStatus.Equal),
                result.Files.Count(f => !f.IsDirectory && f.Status is FileStatus.Modified or FileStatus.LeftOnly or FileStatus.RightOnly),
                result.Files.Count(f => !f.IsDirectory && f.Status == FileStatus.Error))
            + (pending > 0 ? ModularDiff.Gui.Controls.Lang.Get("status_pending_suffix", pending) : "");
    }
    private void CancelClick(object sender, RoutedEventArgs e) { _syncCancellation?.Cancel(); _comparisonCancellation?.Cancel(); _detailCancellation?.Cancel(); }
    private async void SwapClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        (LeftPath.Text, RightPath.Text) = (RightPath.Text, LeftPath.Text);
        // Swapped sides go through the same validated commit as Enter: two files become a text tab
        // (the folder comparison survives), anything else re-compares as the root comparison.
        if (!string.IsNullOrWhiteSpace(LeftPath.Text) && !string.IsNullOrWhiteSpace(RightPath.Text)
            && !string.Equals(LeftPath.Text, RightPath.Text, StringComparison.OrdinalIgnoreCase))
            await CommitPathBarAsync();
    }
    private void SetView(bool text)
    {
        if (!text)
        {
            // Leaving the file view must drop the active text session; otherwise a later click
            // on the same tab early-returns without RestoreSessionState and the path bar stays
            // on directory roots (or the reverse leak after OpenFilePair).
            if (_activeSession is { } current)
            {
                SaveSessionState(current);
                _activeSession = null;
            }
            foreach (var session in _textSessions)
                if (session.TabButton is not null) session.TabButton.IsChecked = false;
        }
        FolderView.Visibility = text ? Visibility.Collapsed : Visibility.Visible;
        TextView.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
        SyncViewChrome();
    }

    /// <summary>Keeps shared chrome (path bar, toolbar enablement, status strip) aligned with
    /// FolderView vs TextView. File tabs overwrite paths/hunks; returning to the folder tab
    /// must restore directory roots and clear file-only indicators.</summary>
    private void SyncViewChrome()
    {
        bool text = TextView.Visibility == Visibility.Visible;
        if (!text)
        {
            FolderTab.IsChecked = true;
            RestoreFolderPathBar();
            HunkLabel.Text = "";
            FileStatsLabel.Text = "";
        }
        OpenButton.IsEnabled = !text && _folderResult is not null;
        PreviousFileButton.IsEnabled = NextFileButton.IsEnabled = text && _folderResult is not null;
        ExportButton.IsEnabled = text ? _fileResult is not null : _folderResult is not null;
        UpdateSummary();
        UpdateFileStats();
        UpdateEditState();
        if (!text) UpdateFolderSelectionStatus();   // back on the Folder tab: describe the selection again
    }
    /// <summary>Recomputes the rows of the folder grids from the status filter, the name filter
    /// and the expand state, keeping each side's own selection. The lists keep their row visuals
    /// and only point them at the nodes now at each position.</summary>
    private void RefreshFolderRows(FolderNode? selected = null)
    {
        if (!_ready || _tree is null) return;
        selected ??= LeftFolders.SelectedItem as FolderNode;
        var nameFilter = CurrentFolderNameFilter();
        _folderRows.Clear();
        _folderRows.AddRange(_tree.Visible(FolderFilter.SelectedIndex, nameFilter));
        _syncFolderSelection = true;
        LeftFolders.SetRows(_folderRows); RightFolders.SetRows(_folderRows);
        // 两侧各自保留自己的选择(仍在可见行内);此前统一到同一行会抹掉跨文件选择的一侧。
        FolderNode? Pick(FolderNode? current, FolderNode? fallback) =>
            current is not null && _folderRows.Contains(current) ? current
            : fallback is not null && _folderRows.Contains(fallback) ? fallback
            : _folderRows.Count > 0 ? _folderRows[0] : null;
        LeftFolders.SelectedItem = Pick(LeftFolders.SelectedItem as FolderNode, selected);
        RightFolders.SelectedItem = Pick(RightFolders.SelectedItem as FolderNode, selected);
        _syncFolderSelection = false;
        UpdateFolderSummary();
        if (nameFilter.Error is not null)
            StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Text(nameFilter.Error);
    }
    private FolderNameFilter CurrentFolderNameFilter() =>
        FolderNameFilter.Create(FolderSearch.Text, FolderSearchRegex.IsChecked == true);
    private void FolderFilterChanged(object sender, SelectionChangedEventArgs e) { RefreshFolderRows(); ScrollFoldersToTop(); }
    private void FolderSearchChanged(object sender, TextChangedEventArgs e) { RefreshFolderRows(); ScrollFoldersToTop(); }
    /// <summary>A changed filter starts a new result at the top; refreshing for other reasons
    /// (progressive scan, expand/collapse) must not pull the user back up.</summary>
    private void ScrollFoldersToTop()
    {
        if (!_ready || _tree is null) return;   // the XAML raises the filter events while the window is still being built
        LeftFolders.ScrollToTop(); RightFolders.ScrollToTop();
    }
    private void FolderSearchRegexChanged(object sender, RoutedEventArgs e) => RefreshFolderRows();
    private void ClearFolderSearch(object sender, RoutedEventArgs e) => FolderSearch.Clear();
    private void ExpandAll(object sender, RoutedEventArgs e) { if (_tree is null) return; foreach (var node in _tree.All) node.IsExpanded = true; RefreshFolderRows(); }
    private void CollapseAll(object sender, RoutedEventArgs e) { if (_tree is null) return; foreach (var node in _tree.All) node.IsExpanded = false; RefreshFolderRows(); }
    private void ToggleFolder(FolderNode node) { if (!node.IsDirectory) return; node.IsExpanded = !node.IsExpanded; RefreshFolderRows(node); }
    private void FolderExpanderClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FolderNode { IsDirectory: true } node) { ToggleFolder(node); e.Handled = true; }
    }
    private void FolderSelectionChanged(object? sender, EventArgs e)
    {
        if (_syncFolderSelection) return;
        // The status line belongs to the view on screen. Folder selection also changes while a text
        // tab is active (grids re-bound on a language switch, refreshes); that must not replace the
        // text tab's status with a folder row description.
        if (FolderView.Visibility == Visibility.Visible) UpdateFolderSelectionStatus();
    }

    private void UpdateFolderSelectionStatus()
    {
        var left = LeftFolders.SelectedItem as FolderNode;
        var right = RightFolders.SelectedItem as FolderNode;
        if (IsCrossPair(left, right))
            StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_will_compare", left!.Name, right!.Name);
        else if (left is FolderNode node || right is FolderNode)
            StatusLabel.Text = (left ?? right)!.Description;
    }

    private static bool IsCrossPair(FolderNode? left, FolderNode? right) =>
        left is { IsDirectory: false } && right is { IsDirectory: false }
        && left.LeftExists && right.RightExists
        && !string.Equals(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase);

    private void FolderScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (!_ready || _syncFolderScroll || e.VerticalChange == 0 || e.OriginalSource is not ScrollViewer source) return;
        var target = FindChild<ScrollViewer>(ReferenceEquals(sender, LeftFolders) ? RightFolders : LeftFolders);
        if (target is null) return;
        _syncFolderScroll = true; target.ScrollToVerticalOffset(source.VerticalOffset); _syncFolderScroll = false;
    }
    /// <summary>Second click of a double-click: open with the clicked row selected (the list
    /// does not toggle the selection away under a double-click's second click).</summary>
    private async void FolderPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || e.ChangedButton != MouseButton.Left) return;
        if (sender is not Controls.FolderList { } list || list.RowAt(e) is not FolderNode node) return;
        e.Handled = true;
        if (node.IsDirectory) { ToggleFolder(node); return; }
        // 双击的行已在其所在格的选区内;打开语义统一交给 OpenSelectedFiles 按两侧各自选择解析
        // (两侧不同文件 → 跨文件比较;仅一侧 → 相对路径配对)。
        await OpenSelectedFiles();
    }
    private void FolderDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Opening is handled in PreviewMouseLeftButtonDown (ClickCount==2) so selection
        // is not wiped; keep this handler to mark the event handled on the bubble route.
        if (sender is Controls.FolderList { } list && list.RowAt(e) is not null)
            e.Handled = true;
    }
    private async void FolderKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Controls.FolderList { SelectedItem: FolderNode node }) return;
        if (e.Key == Key.Enter) { e.Handled = true; if (node.IsDirectory) ToggleFolder(node); else await OpenSelectedFiles(); }
        else if (node.IsDirectory && e.Key is Key.Left or Key.Right)
        { node.IsExpanded = e.Key == Key.Right; RefreshFolderRows(node); e.Handled = true; }
    }
    private async void OpenSelectedClick(object sender, RoutedEventArgs e) => await OpenSelectedFiles();

    /// <summary>Opens the left/right folder selections. Different files picked on the two
    /// sides (plain clicks — no modifier needed) → cross-name compare; same or single-side
    /// selection → paired open by relative path.</summary>
    private async Task OpenSelectedFiles()
    {
        var leftNode = LeftFolders.SelectedItem as FolderNode;
        var rightNode = RightFolders.SelectedItem as FolderNode;
        if (leftNode is { IsDirectory: true } && (rightNode is null || ReferenceEquals(leftNode, rightNode)))
        { ToggleFolder(leftNode); return; }
        if (IsCrossPair(leftNode, rightNode))
        {
            await OpenFilePair(leftNode!, rightNode!);
            return;
        }
        var node = leftNode is { IsDirectory: false } ? leftNode
            : rightNode is { IsDirectory: false } ? rightNode : null;
        if (node is not null) await OpenNode(node);
    }

    private async Task OpenNode(FolderNode node)
    {
        if (_folderResult is null) return;
        string left = Path.Combine(_folderResult.LeftPath, node.RelativePath);
        string right = Path.Combine(_folderResult.RightPath, node.RelativePath);
        await OpenFilePair(node, node, left, right, pairedSamePath: true);
    }

    private async Task OpenFilePair(FolderNode leftNode, FolderNode rightNode)
    {
        if (_folderResult is null) return;
        string left = Path.Combine(_folderResult.LeftPath, leftNode.RelativePath);
        string right = Path.Combine(_folderResult.RightPath, rightNode.RelativePath);
        await OpenFilePair(leftNode, rightNode, left, right, pairedSamePath: false);
    }

    private async Task OpenFilePair(FolderNode leftNode, FolderNode rightNode, string left, string right, bool pairedSamePath)
    {
        if (_folderResult is null || leftNode.IsDirectory || rightNode.IsDirectory) return;
        bool loaded = await LoadFilePairAsync(left, right,
            relativePath: pairedSamePath ? leftNode.RelativePath : leftNode.Name + " ↔ " + rightNode.Name,
            knownError: pairedSamePath ? leftNode.File : null, sourceNode: leftNode);
        if (!loaded || !pairedSamePath) return;
        _syncFolderSelection = true;
        LeftFolders.SelectedItem = RightFolders.SelectedItem = leftNode;
        _syncFolderSelection = false;
    }

    /// <summary>Compares one file pair and shows it as a text tab — reusing the tab already open
    /// for that pair, or the active blank tab. The folder comparison is left untouched, so a pair
    /// typed into the path bar never wipes the Folder tab. Returns false when the user cancelled the
    /// unsaved-edits prompt or the load failed; the tab shown before is then put back on screen.</summary>
    private async Task<bool> LoadFilePairAsync(string left, string right, string relativePath, FileComparison? knownError, FolderNode? sourceNode)
    {
        if (_busy || _folderResult is null || _rootRequest is null) return false;
        if (!await CanLeaveEdits()) return false;
        var previous = _activeSession;
        // The tab being left keeps its own identity: the boxes may already hold the incoming pair.
        if (previous is not null) SaveSessionState(previous, adoptBoxes: false);
        ClearEditors();
        _detailCancellation?.Cancel();
        var cancellation = new CancellationTokenSource(); _detailCancellation = cancellation;
        var token = cancellation.Token;
        var request = _rootRequest;
        _currentNode = sourceNode; _textFile = null; _fileResult = null; _presentation = null;
        LeftCode.SetRows([]); RightCode.SetRows([]); Overview.SetPresentation(null);
        HexHost.Visibility = Visibility.Collapsed; PictureHost.Visibility = Visibility.Collapsed;
        TextEmptyMessage.Text = ModularDiff.Gui.Controls.Lang.Get("text_loading"); TextEmptyState.Visibility = Visibility.Visible;
        CancelButton.IsEnabled = true; ActivityLabel.Text = ModularDiff.Gui.Controls.Lang.Get("activity_loading_file");
        string? empty = null;
        bool loaded = false, superseded = false;
        try
        {
            bool leftMissing = !File.Exists(left);
            bool rightMissing = !File.Exists(right);
            if (leftMissing && rightMissing) throw new InvalidOperationException(ModularDiff.Gui.Controls.Lang.Get("status_both_missing"));
            ComparisonResult result;
            if (knownError is { Status: FileStatus.Error })
                result = new(left, right, "files", request.AlgorithmId, [knownError], []);
            else
            {
                if (leftMissing || rightMissing) empty = Path.GetTempFileName();
                var fileRequest = request with
                {
                    LeftPath = leftMissing ? empty! : left,
                    RightPath = rightMissing ? empty! : right,
                };
                result = await Task.Run(() => _engine.CompareAsync(fileRequest, "files", cancellationToken: token), token);
                var file = result.Files.Single() with { RelativePath = relativePath };
                if (leftMissing) file = file with { Status = FileStatus.RightOnly };
                else if (rightMissing) file = file with { Status = FileStatus.LeftOnly };
                result = result with { LeftPath = left, RightPath = right, Files = [file] };
            }
            token.ThrowIfCancellationRequested();
            _fileResult = result; _textFile = result.Files.Single();
            SetTextHeaders(left, right, _textFile);
            await RebuildPresentation(token);
            await AttachEditors(leftMissing ? empty! : left, rightMissing ? empty! : right, token);
            LeftPath.Text = left; RightPath.Text = right;
            RememberCommittedPaths(left, right);
            var session = FindOrAdoptSession(left, right);
            session.SourceNode = sourceNode;
            SaveSessionState(session);
            ActivateTextSession(session);
            if (_textFile is { IsBinary: true }) BindBinaryView(session);
            ExportButton.IsEnabled = true; UpdateSummary(); LeftCode.Focus();
            loaded = true;
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_detailCancellation, cancellation)) TextEmptyMessage.Text = ModularDiff.Gui.Controls.Lang.Get("load_cancelled");
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) { TextEmptyMessage.Text = ModularDiff.Gui.Controls.Lang.Get("status_load_file_failed", ModularDiff.Gui.Controls.Lang.Text(ex.Message)); StatusLabel.Text = TextEmptyMessage.Text; }
        }
        finally
        {
            superseded = !ReferenceEquals(_detailCancellation, cancellation) && token.IsCancellationRequested;
            if (empty is not null) try { File.Delete(empty); } catch (IOException) { }
            if (ReferenceEquals(_detailCancellation, cancellation)) { _detailCancellation = null; CancelButton.IsEnabled = false; ActivityLabel.Text = ModularDiff.Gui.Controls.Lang.Get("activity_ready"); }
            cancellation.Dispose();
        }
        // A failed load must not leave the tab that was on screen blank: put its content back; the
        // reason stays in the status bar. (Not when a newer load superseded this one — that load
        // owns the panes now.)
        if (!loaded && !superseded && previous is not null && ReferenceEquals(previous, _activeSession))
        {
            string reason = StatusLabel.Text;
            await RestoreSessionStateAsync(previous);
            StatusLabel.Text = reason;
        }
        return loaded;
    }
    private void SetTextHeaders(string left, string right, FileComparison file)
    {
        LeftFileTitle.Text = left; LeftFileTitle.ToolTip = left; RightFileTitle.Text = right; RightFileTitle.ToolTip = right;
        UpdateTextHeaders(file);
        // Tab titles are owned by session create/activate — do not rewrite the previous
        // tab's title while a new file is still loading.
    }
    /// <summary>Line counts under the file titles; recomputed when the language changes.</summary>
    private void UpdateTextHeaders(FileComparison? file)
    {
        if (file is null) { LeftFileInfo.Text = ""; RightFileInfo.Text = ""; return; }
        LeftFileInfo.Text = file.Status == FileStatus.RightOnly ? ModularDiff.Gui.Controls.Lang.Get("status_file_right_only") : ModularDiff.Gui.Controls.Lang.Get("status_lines_left", file.Lines.Count(l => l.LeftLine is not null));
        RightFileInfo.Text = file.Status == FileStatus.LeftOnly ? ModularDiff.Gui.Controls.Lang.Get("status_file_right_only") : ModularDiff.Gui.Controls.Lang.Get("status_lines_right", file.Lines.Count(l => l.RightLine is not null));
    }
    /// <summary>Empty-state text and the hint line under the panes, both derived from the current
    /// file; plugin notes are routed through Lang.Text so they follow the UI language.</summary>
    private void RefreshFileMessages()
    {
        if (_textFile is not { } file) { DetailLabel.Text = ""; TextEmptyMessage.Text = ""; return; }
        TextEmptyMessage.Text = file.Status == FileStatus.Error ? (file.Note is { } error ? ModularDiff.Gui.Controls.Lang.Text(error) : ModularDiff.Gui.Controls.Lang.Get("file_read_error"))
            : file.IsBinary ? ModularDiff.Gui.Controls.Lang.Get("binary_no_text", file.Note is { } note ? ModularDiff.Gui.Controls.Lang.Text(note) : "")
            : ModularDiff.Gui.Controls.Lang.Get("empty_file");
        DetailLabel.Text = (file.IsApproximate ? ModularDiff.Gui.Controls.Lang.Get("approx_align_prefix") : "")
            + (file.Note is { } detail ? ModularDiff.Gui.Controls.Lang.Text(detail) : ModularDiff.Gui.Controls.Lang.Get("detail_nav_hint"));
    }
    /// <summary>The difference counter left of the status message, for text, hex and picture views alike.</summary>
    private void RefreshHunkLabel()
    {
        if (_presentation is { } presentation)
            HunkLabel.Text = _currentHunk >= 0 && _currentHunk < presentation.Hunks.Count
                ? ModularDiff.Gui.Controls.Lang.Get("hunks_index", _currentHunk + 1, presentation.Hunks.Count)
                : ModularDiff.Gui.Controls.Lang.Get("hunks_count", presentation.Hunks.Count);
        else if (PictureHost.Visibility == Visibility.Visible && _textFile?.Note is { } pictureNote) HunkLabel.Text = ModularDiff.Gui.Controls.Lang.Text(pictureNote);
        else if (HexHost.Visibility == Visibility.Visible && _textFile?.BinaryDiffs is { } diffs) HunkLabel.Text = ModularDiff.Gui.Controls.Lang.Get("byte_diffs", diffs.Count);
        else HunkLabel.Text = "";
    }
    private static readonly HashSet<string> PictureExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    /// <summary>Routes the current file's extension to a syntax highlighter from the
    /// IFileTypeProvider plugins and installs it on both DiffPanes.</summary>
    private void UpdateSyntaxHighlighter()
    {
        string? path = _textFile?.RelativePath is { } rel ? rel : _activeSession?.LeftPath;
        if (string.IsNullOrWhiteSpace(path)) { LeftCode.SyntaxHighlighter = RightCode.SyntaxHighlighter = null; return; }
        string extension = System.IO.Path.GetExtension(path);
        // Priority 1: ISyntaxGrammar plugins (declarative grammar JSON files, VS Code-style).
        var grammar = _plugins.GetAll<ISyntaxGrammar>()
            .FirstOrDefault(g => g.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
        if (grammar is not null)
        {
            LeftCode.SyntaxHighlighter = RightCode.SyntaxHighlighter = line => grammar.Tokenize(line, extension) ?? [];
            return;
        }
        // Priority 2: IFileTypeProvider.HighlightLine (for plugins that implement their own tokenizing).
        var provider = _plugins.GetAll<IFileTypeProvider>()
            .FirstOrDefault(p => p.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
        if (provider is not null)
        {
            LeftCode.SyntaxHighlighter = RightCode.SyntaxHighlighter = line => provider.HighlightLine(line) ?? [];
            return;
        }
        // Priority 3: DiffPane's built-in C-style regex (fallback).
        LeftCode.SyntaxHighlighter = RightCode.SyntaxHighlighter = null;
    }

    private async Task RebuildPresentation(CancellationToken token = default, bool preservePosition = false)
    {
        var file = _textFile; if (file is null) return;
        UpdateSyntaxHighlighter();
        if (file.IsBinary)
        {
            if (PictureExtensions.Contains(Path.GetExtension(_fileResult?.LeftPath ?? ""))) await ShowPictureAsync(file);
            else await ShowHexAsync(token);
            RefreshFileMessages(); RefreshHunkLabel();
            return;
        }
        HexHost.Visibility = Visibility.Collapsed; PictureHost.Visibility = Visibility.Collapsed;
        if (PresentationBox.SelectedItem is not ITextPresentation presenter) throw new InvalidOperationException(ModularDiff.Gui.Controls.Lang.Get("status_no_presentation"));
        long version = _presentationVersion;   // session manages version increments internally
        int context = TextFilter.SelectedIndex switch { 1 => 0, 2 => 3, _ => -1 };
        var presentation = await Task.Run(() => presenter.Build(file.Lines, context, token), token);
        token.ThrowIfCancellationRequested();
        if (version != _presentationVersion || !ReferenceEquals(file, _textFile)) return;
        _presentation = presentation; _currentHunk = -1;
        _syncText = true; LeftCode.SetRows(presentation.Rows, !preservePosition); RightCode.SetRows(presentation.Rows, !preservePosition); _syncText = false;
            Overview.SetPresentation(presentation); UpdateOverview();
            RefreshFileMessages(); RefreshHunkLabel();
            UpdateEditState(); UpdateFileStats();
            TextEmptyState.Visibility = presentation.Rows.Count == 0 && _leftBuffer is null ? Visibility.Visible : Visibility.Collapsed;
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
            Hex.SetContent(left, right, file.BinaryDiffs!, editablePath: result.LeftPath);
            PictureHost.Visibility = Visibility.Collapsed;   // one binary pane at a time, whichever tab is active
            HexHost.Visibility = Visibility.Visible;
            if (file.BinaryDiffs!.Count > 0)
                Hex.SetVerticalOffset(Math.Max(0, file.BinaryDiffs[0].Offset / 16 * Hex.RowHeight - 120));
            StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_hex_mode");
            // Patch export and offset navigation via the hex view.
            Hex.MouseRightButtonUp -= HexContextMenu;
            Hex.MouseRightButtonUp += HexContextMenu;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            HexHost.Visibility = Visibility.Collapsed;
            StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_hex_load_failed", ModularDiff.Gui.Controls.Lang.Text(ex.Message));
        }
    }

    private void HexContextMenu(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();
        var export = new MenuItem { Header = ModularDiff.Gui.Controls.Lang.Get("hex_export_report") };
        export.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Text report|*.txt", FileName = "binary-patch.txt" };
            if (dialog.ShowDialog(this) == true)
                try { System.IO.File.WriteAllText(dialog.FileName, Hex.ExportPatch()); StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_report_saved", dialog.FileName); }
                catch (Exception ex) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_export_failed", ModularDiff.Gui.Controls.Lang.Text(ex.Message)); }
        };
        menu.Items.Add(export);
        var goTo = new MenuItem { Header = ModularDiff.Gui.Controls.Lang.Get("hex_goto_offset") };
        goTo.Click += (_, _) => HexGoToOffset();
        menu.Items.Add(goTo);
        menu.PlacementTarget = Hex;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void HexGoToOffset()
    {
        if (!Hex.HasContent) return;
        string? input = PromptLineNumber((int)Math.Min(Hex.Rows * 16, int.MaxValue));
        if (input is null) return;
        if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(input[2..], System.Globalization.NumberStyles.HexNumber, null, out long hexOffset))
            Hex.GoToOffset(hexOffset);
        else if (long.TryParse(input, out long decOffset))
            Hex.GoToOffset(decOffset);
        else StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_bad_offset");
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
            StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_picture_mode");
        }
        catch (Exception ex)
        {
            PictureHost.Visibility = Visibility.Collapsed;
            StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_picture_failed", ModularDiff.Gui.Controls.Lang.Text(ex.Message));
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
        try { await RebuildPresentation(); } catch (Exception ex) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Text(ex.Message); }
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
            HunkLabel.Text = _currentHunk >= 0 ? ModularDiff.Gui.Controls.Lang.Get("hunks_index", _currentHunk + 1, _presentation.Hunks.Count) : ModularDiff.Gui.Controls.Lang.Get("hunks_count", _presentation.Hunks.Count);
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

    private void LangBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The dropdown selects a language explicitly. Select applies it and remembers the choice
        // for the next launch; LocalizeUI is invoked by Lang.Changed.
        if (LangBox.SelectedItem is string name && !string.Equals(name, Controls.Lang.CurrentName, StringComparison.Ordinal))
            Controls.Lang.Select(name);
    }

    /// <summary>Surfaces grammar/theme/language file load errors to the user instead of silently
    /// skipping them — each engine reports file, line, column so problems are immediately fixable.</summary>
    private void ReportDeclarativeFileIssues()
    {
        var issues = new List<string>();
        // Grammar files (via ISyntaxGrammar.LoadDiagnostics — works for any syntax plugin)
        foreach (var grammar in _plugins.GetAll<ISyntaxGrammar>())
            issues.AddRange(grammar.LoadDiagnostics);
        // Language packs
        issues.AddRange(Controls.Lang.LoadDiagnostics);
        // Theme files
        issues.AddRange(Controls.Theme.LoadDiagnostics);
        if (issues.Count > 0)
        {
            string joined = string.Join("\n  ", issues);
            StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_declarative_failed", issues.Count);
            MessageBox.Show(this, ModularDiff.Gui.Controls.Lang.Get("dlg_declarative_body", joined), ModularDiff.Gui.Controls.Lang.Get("dlg_declarative_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ThemeToggleClick(object sender, RoutedEventArgs e)
    {
        ModularDiff.Gui.Controls.Theme.SetDark(ModularDiff.Gui.Controls.Theme.CurrentName != "Dark");
        ThemeToggle.Content = ModularDiff.Gui.Controls.Theme.CurrentName == "Dark" ? "\uE708" : "\uE706";
        Background = ModularDiff.Gui.Controls.Theme.WindowBackground;
        LeftCode.InvalidateVisual(); RightCode.InvalidateVisual();
        StatusLabel.Text = ModularDiff.Gui.Controls.Theme.CurrentName == "Dark"
            ? ModularDiff.Gui.Controls.Lang.Get("status_dark_on")
            : ModularDiff.Gui.Controls.Lang.Get("status_dark_off");
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
            { LeftCode.SelectRow(index); RightCode.SelectRow(index); CodeSelected(index); StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_found_query", FindBox.Text); return; }
        }
        StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_not_found_query", FindBox.Text);
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
        if (pane.Buffer is null) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_readonly_file"); return; }
        if (FindBox.Text.Length == 0) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_enter_find"); return; }
        string text = pane.Buffer.Text;
        int from = Math.Min(Math.Max(pane.CaretOffset, 0), text.Length);
        int index = text.IndexOf(FindBox.Text, from, StringComparison.OrdinalIgnoreCase);
        if (index < 0) index = text.IndexOf(FindBox.Text, StringComparison.OrdinalIgnoreCase);
        if (index < 0) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_not_found_query", FindBox.Text); return; }
        pane.Focus();
        pane.SetCaret(index, false, false);
        pane.SetCaret(index + FindBox.Text.Length, true, false);
        pane.ReplaceSelection(ReplaceBox.Text, "Replace");
        await _editTask;
        StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_replaced_one", FindBox.Text, ReplaceBox.Text);
    }
    private async void ReplaceAllClick(object sender, RoutedEventArgs e)
    {
        var pane = _activePane ?? LeftCode;
        if (pane.Buffer is null) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_readonly_file"); return; }
        if (FindBox.Text.Length == 0) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_enter_find"); return; }
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
        if (count == 0) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_not_found_query", FindBox.Text); return; }
        pane.Buffer.Apply(new Contracts.TextEdit(0, text.Length, builder.ToString(), ModularDiff.Gui.Controls.Lang.Get("edit_desc_replace_all", count)));
        AfterProgrammaticEdit(pane, ModularDiff.Gui.Controls.Lang.Get("status_replaced_n", count, FindBox.Text, ReplaceBox.Text));
        await _editTask;
    }
    private async void WindowKeyDown(object sender, KeyEventArgs e)
    {
        // Hex view shortcuts take priority when visible
        if (HexHost.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                if (Hex.IsDirty && !Hex.Save()) StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_hex_save_failed");
                else if (Hex.IsDirty) StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_hex_saved");
                return;
            }
            if (e.Key == Key.G && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                HexGoToOffset();
                return;
            }
        }
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
        else if (e.Key == Key.Escape) { CancelAlignWith(); SettingsToggle.IsChecked = false; CancelClick(sender, e); }
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
        var dialog = new SaveFileDialog { Filter = $"{ModularDiff.Gui.Controls.Lang.Text(formatter.DisplayName)}|*{formatter.FileExtension}", DefaultExt = formatter.FileExtension, FileName = "comparison" + formatter.FileExtension };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            string output = Path.GetFullPath(dialog.FileName);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (string input in new[] { result.LeftPath, result.RightPath, _rootRequest?.LeftPath, _rootRequest?.RightPath }.OfType<string>())
                if (output.Equals(input, comparison) || (Directory.Exists(input) && output.StartsWith(Path.TrimEndingDirectorySeparator(input) + Path.DirectorySeparatorChar, comparison)))
                    throw new InvalidOperationException(ModularDiff.Gui.Controls.Lang.Get("err_export_outside"));
            await using var writer = new StreamWriter(output, false, new UTF8Encoding(false));
            await formatter.WriteAsync(result, writer); StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_export_saved", output);
        }
        catch (Exception ex) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_export_failed", ModularDiff.Gui.Controls.Lang.Text(ex.Message)); }
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
                if (_folderResult.HasErrors || !ReferenceEquals(LeftFolders.Rows, RightFolders.Rows)) throw new InvalidOperationException("Folder panes are not aligned.");
                // Path bar Enter: invalid / mixed kinds restore committed paths and keep the result.
                string okLeft = LeftPath.Text, okRight = RightPath.Text;
                LeftPath.Text = Path.Combine(okLeft, "__no_such_path__");
                await CommitPathBarAsync();
                if (!string.Equals(LeftPath.Text, okLeft, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(RightPath.Text, okRight, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Invalid path-bar Enter must restore committed paths.");
                if (_folderResult is null) throw new InvalidOperationException("Invalid path-bar Enter must not clear the comparison.");
                string? rightFile = Directory.EnumerateFiles(okRight, "*", SearchOption.AllDirectories).FirstOrDefault();
                if (rightFile is not null)
                {
                    LeftPath.Text = okLeft;
                    RightPath.Text = rightFile;
                    await CommitPathBarAsync();
                    if (!string.Equals(LeftPath.Text, okLeft, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(RightPath.Text, okRight, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("File+directory path-bar Enter must restore committed paths.");
                }
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (screenshot is not null) SaveScreenshot(screenshot);
                var folder = _folderRows.FirstOrDefault(n => n.IsDirectory);
                if (folder is not null)
                {
                    if (!folder.IsExpanded) ToggleFolder(folder); // trees start collapsed
                    int expanded = _folderRows.Count; ToggleFolder(folder);
                    if (_folderRows.Count >= expanded) throw new InvalidOperationException("Collapse did not hide descendants.");
                    ToggleFolder(folder);
                }
                ExpandAll(this, new RoutedEventArgs()); // need nested files visible for open/tab checks
                FolderFilter.SelectedIndex = 1;
                if (_folderRows.Any(n => !n.IsDirectory && n.Status == FileStatus.Equal)) throw new InvalidOperationException("Difference filter failed.");
                FolderFilter.SelectedIndex = 0;
                var node = _folderRows.FirstOrDefault(n => !n.IsDirectory && n.Status == FileStatus.Modified && n.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) ?? _folderRows.First(n => !n.IsDirectory);
                LeftFolders.SelectedItem = node;
                await OpenNode(node);
                if (_textFile is null) throw new InvalidOperationException("Opening directory file failed.");
                if (TextView.Visibility != Visibility.Visible) throw new InvalidOperationException("OpenNode must switch to the text view.");
                if (_textSessions.Count != 1) throw new InvalidOperationException("First folder file open must create exactly one text tab.");
                string firstLeft = Path.Combine(_folderResult.LeftPath, node.RelativePath);
                if (!string.Equals(_textSessions[0].LeftPath, firstLeft, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("First text tab stored the wrong left path.");
                // Second distinct file must open as an independent tab (not reuse/clobber the first).
                var node2 = _folderRows.FirstOrDefault(n => !n.IsDirectory && !ReferenceEquals(n, node) && n.Status != FileStatus.Equal)
                    ?? _folderRows.FirstOrDefault(n => !n.IsDirectory && !ReferenceEquals(n, node));
                if (node2 is not null)
                {
                    await OpenNode(node2);
                    if (_textSessions.Count != 2)
                        throw new InvalidOperationException($"Expected 2 independent file tabs, got {_textSessions.Count}.");
                    string secondLeft = Path.Combine(_folderResult.LeftPath, node2.RelativePath);
                    if (!string.Equals(_textSessions[0].LeftPath, firstLeft, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Opening a second file corrupted the first tab's path identity.");
                    if (!string.Equals(_textSessions[1].LeftPath, secondLeft, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Second text tab stored the wrong left path.");
                    await SwitchToSession(_textSessions[0]);
                    if (!string.Equals(LeftPath.Text, firstLeft, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Switching back to the first tab did not restore its paths.");
                    // 用户场景回归:普通点击(无修饰键)左右各选一个不同文件 → 打开必须是跨文件比较,
                    // 绝不允许退化成"最后一个文件 vs 它自己"(旧镜像选择的行为)。
                    if (node.LeftExists && node2.RightExists
                        && !string.Equals(node.RelativePath, node2.RelativePath, StringComparison.OrdinalIgnoreCase))
                    {
                        SetView(false);
                        // 走生产通路:两侧各自设 SelectedItem 触发 FolderSelectionChanged(与普通点击同一事件),再解析打开。
                        LeftFolders.SelectedItem = node;
                        RightFolders.SelectedItem = node2;
                        await OpenSelectedFiles();
                        string crossLeft = Path.Combine(_folderResult.LeftPath, node.RelativePath);
                        string crossRight = Path.Combine(_folderResult.RightPath, node2.RelativePath);
                        if (!string.Equals(LeftPath.Text, crossLeft, StringComparison.OrdinalIgnoreCase)
                            || !string.Equals(RightPath.Text, crossRight, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Cross-name folder open did not load the selected left/right files.");
                        if (_textSessions.Count < 3)
                            throw new InvalidOperationException("Cross-name compare must open as its own tab.");
                        await SwitchToSession(_textSessions[0]); // restore paired file for final hunk checks
                    }
                }
                LeftFolders.SelectedItem = RightFolders.SelectedItem = node;
                SetView(false);
                if (!string.Equals(LeftPath.Text, _folderResult.LeftPath, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(RightPath.Text, _folderResult.RightPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Returning to folder view must restore directory roots in the path bar.");
                if (HunkLabel.Text.Length > 0)
                    throw new InvalidOperationException("Folder view must clear file hunk status.");
                if (!OpenButton.IsEnabled || PreviousFileButton.IsEnabled || NextFileButton.IsEnabled)
                    throw new InvalidOperationException("Folder view toolbar enablement is wrong (Open on, Prev/Next off).");
                if (SaveLeftButton.IsEnabled || SaveRightButton.IsEnabled)
                    throw new InvalidOperationException("Folder view must disable path-bar save buttons.");
                if (!ReferenceEquals(LeftFolders.SelectedItem, node) || !ReferenceEquals(RightFolders.SelectedItem, node))
                    throw new InvalidOperationException("Folder selection was lost.");
                await SwitchToSession(_textSessions[0]);
                if (!string.Equals(LeftPath.Text, _textSessions[0].LeftPath, StringComparison.OrdinalIgnoreCase)
                    || OpenButton.IsEnabled || !PreviousFileButton.IsEnabled)
                    throw new InvalidOperationException("Returning to a file tab must restore file paths and text-toolbar enablement.");
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
            // 标签页批量关闭回归:关闭其他只剩当前 → 关闭全部必须落在一个两侧空白的比较页上。
            if (_textSessions.Count >= 2)
            {
                var keep = _textSessions[0];
                await CloseOtherTabs(keep);
                if (_textSessions.Count != 1 || !ReferenceEquals(_textSessions[0], keep))
                    throw new InvalidOperationException("CloseOtherTabs must leave exactly the kept tab.");
                await CloseAllTabs();
                if (_textSessions.Count != 1 || _textSessions[0].LeftPath.Length > 0 || _textSessions[0].RightPath.Length > 0)
                    throw new InvalidOperationException("CloseAllTabs must leave exactly one blank tab.");
            }
            // 文本视图下 chrome 最全(含文字工具栏),先在这里跑语言切换;随后验证路径框与标签页宽度。
            await LocalizationSmokeAsync(screenshot);
            await PathBarAndTabChromeSmokeAsync();
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
