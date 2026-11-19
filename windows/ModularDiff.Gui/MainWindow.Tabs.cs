using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ModularDiff.Contracts;
using ModularDiff.Gui.Controls;

namespace ModularDiff.Gui;

/// <summary>Dynamic multi-session tab system: the folder tab is fixed, each file pair opened
/// gets its own text tab with independent comparison state (buffers, presentation, scroll).</summary>
public partial class MainWindow
{
    private sealed class TextSession
    {
        public required string LeftPath, RightPath;
        public string Title = "新比较";
        public ComparisonResult? FileResult;
        public TextPresentation? Presentation;
        public FileComparison? TextFile;
        public FolderNode? SourceNode;
        public IEditBuffer? LeftBuffer, RightBuffer;
        public BufferComparison? BufferDiff;
        public int CurrentHunk = -1;
        public double LeftVOffset, RightVOffset;
        public bool IsDirty;
        public ToggleButton? TabButton;
    }

    private readonly List<TextSession> _textSessions = [];
    private TextSession? _activeSession;

    private void InitializeTabs()
    {
        FolderTab.Click += (_, _) => SwitchToFolderView();
        NewTabButton.Click += async (_, _) => { NewTabButton.IsChecked = false; await CreateTextTab("", ""); };
    }

    /// <summary>Orchestrator: runs the pure comparison, then wraps file results in a session
    /// (creates a tab, stores state, marks it active — without re-triggering comparison).
    /// Folder comparisons switch to the folder view. Session management stays OUT of CompareAsync.</summary>
    private async Task CompareAndTrackSession()
    {
        await CompareAsync();
        if (_fileResult is not null && _rootRequest is not null && !Directory.Exists(_rootRequest.LeftPath))
        {
            var session = _textSessions.FirstOrDefault(s =>
                string.Equals(s.LeftPath, _rootRequest.LeftPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.RightPath, _rootRequest.RightPath, StringComparison.OrdinalIgnoreCase));
            if (session is null)
            {
                session = CreateSession(_rootRequest.LeftPath, _rootRequest.RightPath);
                session.Title = Path.GetFileName(_rootRequest.LeftPath) + " ↔ " + Path.GetFileName(_rootRequest.RightPath);
                BuildTabButton(session);
            }
            SaveSessionState(session);
            ActivateTextSession(session);
        }
        else if (_folderResult is not null && _activeSession is not null)
        {
            SwitchToFolderView();
        }
    }

    private TextSession CreateSession(string left, string right)
    {
        var session = new TextSession { LeftPath = left, RightPath = right };
        _textSessions.Add(session);
        return session;
    }

    private async Task<TextSession> CreateTextTab(string left, string right)
    {
        var session = CreateSession(left, right);
        session.Title = string.IsNullOrWhiteSpace(Path.GetFileName(left)) && string.IsNullOrWhiteSpace(Path.GetFileName(right))
            ? "新比较" : Path.GetFileName(left) + " ↔ " + Path.GetFileName(right);
        BuildTabButton(session);
        await SwitchToSession(session);
        return session;
    }

    private void BuildTabButton(TextSession session)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var label = new TextBlock { Text = session.Title };
        var close = new Button
        {
            Content = "×", Width = 18, Height = 18, FontSize = 13, Margin = new Thickness(5, 0, 0, 0),
            Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "关闭此比较",
        };
        close.Click += (_, _) => _ = CloseTextTab(session);
        panel.Children.Add(label);
        panel.Children.Add(close);
        session.TabButton = new ToggleButton
        {
            Content = panel, Style = (Style)FindResource("SessionTab"), Padding = new Thickness(10, 5, 10, 5),
            ToolTip = session.LeftPath + "\n" + session.RightPath,
        };
        // Tabs are not checkboxes: re-clicking an active tab must not uncheck it (which would
        // leave no tab selected and hide nothing useful). Keep it checked and re-activate.
        session.TabButton.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (session.TabButton.IsChecked == true)
            {
                e.Handled = true;
                _ = SwitchToSession(session);
            }
        };
        session.TabButton.Checked += (_, _) => _ = SwitchToSession(session);
        TabStripPanel.Children.Remove(NewTabButton);
        TabStripPanel.Children.Add(session.TabButton);
        TabStripPanel.Children.Add(NewTabButton);
    }

    private async Task CloseTextTab(TextSession session)
    {
        if (ReferenceEquals(session, _activeSession)) SaveSessionState(session);
        if (session.IsDirty)
        {
            var choice = MessageBox.Show(this, $"「{session.Title}」有未保存的修改。\n\n关闭前保存？", "未保存的修改",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (choice == MessageBoxResult.Cancel) return;
            if (choice == MessageBoxResult.Yes)
            {
                _activeSession = session;
                RestoreSessionState(session);
                // 保存失败必须中止关闭:SaveBuffers 返回 false 时弹窗已承诺"编辑仍保留",
                // 此前无条件移除 session 会把唯一的缓冲区引用丢掉(审查发现的数据丢失路径)。
                if (!await SaveBuffers([session.LeftBuffer, session.RightBuffer])) return;
            }
        }
        _textSessions.Remove(session);
        TabStripPanel.Children.Remove(session.TabButton);
        if (_textSessions.Count > 0) await SwitchToSession(_textSessions[^1]);
        else SwitchToFolderView();
    }

    private void SwitchToFolderView() => SetView(false);

    // RestoreFolderPathBar lives with SyncViewChrome (SetView) so Alt+Left and the folder tab
    // share one path/toolbar restore path.

    /// <summary>Folder tab owns the directory roots. File tabs temporarily overwrite the path
    /// boxes; switching back must restore the comparison roots (not the last opened file).</summary>
    private void RestoreFolderPathBar()
    {
        if (_folderResult is null) return;
        LeftPath.Text = _folderResult.LeftPath;
        RightPath.Text = _folderResult.RightPath;
        RememberCommittedPaths(_folderResult.LeftPath, _folderResult.RightPath);
    }

    private async Task SwitchToSession(TextSession session)
    {
        // Already-active tab clicks must still reveal TextView: OpenNode used to leave
        // FolderView up while checking the new tab, and a second click would otherwise
        // early-return with the file view still collapsed.
        if (ReferenceEquals(session, _activeSession))
        {
            if (TextView.Visibility != Visibility.Visible)
            {
                SetView(true);
                RestoreSessionState(session); // folder chrome may have overwritten path boxes
            }
            return;
        }
        if (_activeSession is { } current) SaveSessionState(current);
        _activeSession = session;
        SetView(true);
        FolderTab.IsChecked = false;
        foreach (var other in _textSessions) other.TabButton!.IsChecked = ReferenceEquals(other, session);
        RestoreSessionState(session);
        // Auto-compare only when the session has no cached results (user clicked an old tab).
        if (session.FileResult is null && File.Exists(session.LeftPath) && File.Exists(session.RightPath))
        {
            LeftPath.Text = session.LeftPath; RightPath.Text = session.RightPath;
            await CompareAsync();
            SaveSessionState(session);
        }
    }

    /// <summary>Marks <paramref name="session"/> as the active text tab and shows the file view
    /// without going through ToggleButton.Checked (which early-returns once _activeSession is set).</summary>
    private void ActivateTextSession(TextSession session)
    {
        _activeSession = session;
        FolderTab.IsChecked = false;
        foreach (var other in _textSessions) other.TabButton!.IsChecked = ReferenceEquals(other, session);
        SetView(true);
    }

    private void SaveSessionState(TextSession session)
    {
        session.FileResult = _fileResult;
        session.Presentation = _presentation;
        session.TextFile = _textFile;
        session.SourceNode = _currentNode;
        session.LeftBuffer = _leftBuffer;
        session.RightBuffer = _rightBuffer;
        session.BufferDiff = _bufferDiff;
        session.CurrentHunk = _currentHunk;
        session.LeftVOffset = LeftCode.VerticalOffset;
        session.RightVOffset = RightCode.VerticalOffset;
        // Path boxes must not rewrite a different file pair's identity.
        // Regression: open A → view A (boxes=A) → open B from folder while boxes still
        // say A → SaveSessionState(B) used to set B's paths to A → find-or-create then
        // treated every later open as the same tab (one file-compare tab forever).
        // Directory roots are also rejected (File.Exists false). Blank "新比较" tabs and
        // the active tab (user may have edited the boxes) may adopt the boxes.
        bool blank = string.IsNullOrWhiteSpace(session.LeftPath) && string.IsNullOrWhiteSpace(session.RightPath);
        bool samePair =
            string.Equals(session.LeftPath, LeftPath.Text, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(session.RightPath, RightPath.Text, StringComparison.OrdinalIgnoreCase);
        bool adoptBoxes = blank || samePair || ReferenceEquals(session, _activeSession);
        if (adoptBoxes && File.Exists(LeftPath.Text)) session.LeftPath = LeftPath.Text;
        if (adoptBoxes && File.Exists(RightPath.Text)) session.RightPath = RightPath.Text;
        session.IsDirty = HasDirtyBuffers;
        string prefix = session.IsDirty ? "● " : "";
        string title = session.Title.TrimStart('●', ' ');
        session.Title = prefix + title;
        if (session.TabButton is not null)
        {
            (session.TabButton.Content as StackPanel)!.Children.OfType<TextBlock>().First().Text = session.Title;
            session.TabButton.ToolTip = session.LeftPath + "\n" + session.RightPath;
        }
    }

    private void RestoreSessionState(TextSession session)
    {
        _fileResult = session.FileResult;
        _presentation = session.Presentation;
        _textFile = session.TextFile;
        _currentNode = session.SourceNode;
        _bufferDiff = session.BufferDiff;
        _currentHunk = session.CurrentHunk;
        _editCancellation?.Cancel();
        _leftBuffer = session.LeftBuffer;
        _rightBuffer = session.RightBuffer;
        LeftCode.AttachBuffer(_leftBuffer);
        RightCode.AttachBuffer(_rightBuffer);
        LeftCode.SetRows(session.Presentation?.Rows ?? []);
        RightCode.SetRows(session.Presentation?.Rows ?? []);
        LeftCode.SetVerticalOffset(session.LeftVOffset);
        RightCode.SetVerticalOffset(session.RightVOffset);
        Overview.SetPresentation(session.Presentation);
        LeftPath.Text = session.LeftPath;
        RightPath.Text = session.RightPath;
        RememberCommittedPaths(session.LeftPath, session.RightPath);
        UpdateSummary(); UpdateEditState(); UpdateFileStats();
        if (session.Presentation is not null)
            HunkLabel.Text = $"{session.Presentation.Hunks.Count} 个差异块";
        else
            HunkLabel.Text = "";
        OpenButton.IsEnabled = false;
        PreviousFileButton.IsEnabled = NextFileButton.IsEnabled = _folderResult is not null;
        ExportButton.IsEnabled = _fileResult is not null;
    }

    private async Task<TextSession> FindOrCreateSession(string left, string right, FolderNode? node = null)
    {
        var existing = _textSessions.FirstOrDefault(s =>
            string.Equals(s.LeftPath, left, StringComparison.OrdinalIgnoreCase)
            && string.Equals(s.RightPath, right, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            await SwitchToSession(existing);
            return existing;
        }
        var session = await CreateTextTab(left, right);
        session.SourceNode = node;
        session.Title = Path.GetFileName(left) + " ↔ " + Path.GetFileName(right);
        if (session.TabButton is not null)
            (session.TabButton.Content as StackPanel)!.Children.OfType<TextBlock>().First().Text = session.Title;
        return session;
    }

    private int InputMode()
    {
        bool hasLeft = File.Exists(LeftPath.Text) || Directory.Exists(LeftPath.Text);
        bool hasRight = File.Exists(RightPath.Text) || Directory.Exists(RightPath.Text);
        return (hasLeft ? 1 : 0) + (hasRight ? 1 : 0);
    }
}
