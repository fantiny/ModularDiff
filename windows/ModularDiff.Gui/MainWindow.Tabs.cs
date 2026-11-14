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
            _activeSession = session;
            FolderTab.IsChecked = false;
            foreach (var other in _textSessions) other.TabButton!.IsChecked = ReferenceEquals(other, session);
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
                await SaveBuffers([session.LeftBuffer, session.RightBuffer]);
            }
        }
        _textSessions.Remove(session);
        TabStripPanel.Children.Remove(session.TabButton);
        if (_textSessions.Count > 0) await SwitchToSession(_textSessions[^1]);
        else SwitchToFolderView();
    }

    private void SwitchToFolderView()
    {
        if (_activeSession is { } current) SaveSessionState(current);
        _activeSession = null;
        FolderView.Visibility = Visibility.Visible;
        TextView.Visibility = Visibility.Collapsed;
        FolderTab.IsChecked = true;
        foreach (var session in _textSessions) session.TabButton!.IsChecked = false;
        UpdateSummary(); UpdateEditState();
    }

    private async Task SwitchToSession(TextSession session)
    {
        if (ReferenceEquals(session, _activeSession)) return;
        if (_activeSession is { } current) SaveSessionState(current);
        _activeSession = session;
        FolderView.Visibility = Visibility.Collapsed;
        TextView.Visibility = Visibility.Visible;
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
        session.LeftPath = LeftPath.Text;
        session.RightPath = RightPath.Text;
        session.IsDirty = HasDirtyBuffers;
        string prefix = session.IsDirty ? "● " : "";
        string title = session.Title.TrimStart('●', ' ');
        session.Title = prefix + title;
        if (session.TabButton is not null)
            (session.TabButton.Content as StackPanel)!.Children.OfType<TextBlock>().First().Text = session.Title;
    }

    private void RestoreSessionState(TextSession session)
    {
        _fileResult = session.FileResult;
        _presentation = session.Presentation;
        _textFile = session.TextFile;
        _currentNode = session.SourceNode;
        _bufferDiff = session.BufferDiff;
        _currentHunk = session.CurrentHunk;
        _presentationVersion++;
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
        UpdateSummary(); UpdateEditState();
        if (session.Presentation is not null)
            HunkLabel.Text = $"{session.Presentation.Hunks.Count} 个差异块";
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
