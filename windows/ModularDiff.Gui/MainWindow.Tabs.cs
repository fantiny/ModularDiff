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
        public string Title = ModularDiff.Gui.Controls.Lang.Get("tab_new_compare");
        /// <summary>The "new comparison" tab: both paths empty. It is adopted by the first pair
        /// compared while it is active (like a browser's new-tab page) and its title comes from the
        /// language pack.</summary>
        public bool IsBlank;
        public TextBlock? Caption;
        /// <summary>File titles above the panes belong to the tab: a blank tab shows none instead of
        /// the titles of whatever was on screen before.</summary>
        public string LeftTitle = "", RightTitle = "", LeftTitleTip = "", RightTitleTip = "";
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
    /// <summary>The hex and picture panes are window-wide (one Hex control for all tabs): this is the
    /// tab whose bytes they currently hold, and which of the two panes is showing.</summary>
    private TextSession? _binarySession;
    private bool _binaryIsPicture;

    private void BindBinaryView(TextSession session)
    {
        _binarySession = session;
        _binaryIsPicture = PictureHost.Visibility == Visibility.Visible;
    }

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
            var session = FindOrAdoptSession(_rootRequest.LeftPath, _rootRequest.RightPath);
            SaveSessionState(session);
            ActivateTextSession(session);
            if (_textFile is { IsBinary: true }) BindBinaryView(session);
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

    private static string PairCaption(string left, string right) => Path.GetFileName(left) + " ↔ " + Path.GetFileName(right);

    /// <summary>The tab showing this pair — or, when the active tab is a blank "new comparison",
    /// that tab adopted for the pair; otherwise a new tab. A blank tab has no identity of its own
    /// (it adopts whatever is in the path boxes while active), so it never matches by path.</summary>
    private TextSession FindOrAdoptSession(string left, string right)
    {
        var session = _textSessions.FirstOrDefault(s => !s.IsBlank
            && string.Equals(s.LeftPath, left, StringComparison.OrdinalIgnoreCase)
            && string.Equals(s.RightPath, right, StringComparison.OrdinalIgnoreCase));
        if (session is not null) return session;
        if (_activeSession is { IsBlank: true } blank)
        {
            blank.IsBlank = false;
            blank.LeftPath = left; blank.RightPath = right;
            blank.Title = PairCaption(left, right);
            return blank;
        }
        session = CreateSession(left, right);
        session.Title = PairCaption(left, right);
        BuildTabButton(session);
        return session;
    }

    private async Task<TextSession> CreateTextTab(string left, string right)
    {
        var session = CreateSession(left, right);
        session.IsBlank = string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right);
        session.Title = session.IsBlank ? ModularDiff.Gui.Controls.Lang.Get("tab_new_compare") : PairCaption(left, right);
        BuildTabButton(session);
        await SwitchToSession(session);
        return session;
    }

    private void BuildTabButton(TextSession session)
    {
        // DockPanel rather than StackPanel: the caption is the fill child, so it is measured with
        // the width left beside the × and trims with an ellipsis under the tab's MaxWidth. In a
        // StackPanel it would get infinite width and push the × out of the tab.
        var panel = new DockPanel { LastChildFill = true };
        var close = new Button
        {
            Content = "×", Width = 22, Height = 22, FontSize = 14, Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Loc.SetTipKey(close, "tab_close_this");
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        DockPanel.SetDock(close, Dock.Right);
        panel.Children.Add(close);
        panel.Children.Add(label);
        session.Caption = label;
        session.TabButton = new ToggleButton
        {
            Content = panel, Style = (Style)FindResource("SessionTab"), Padding = new Thickness(10, 5, 10, 5),
        };
        // 一个预览处理器按命中目标分流:
        // · 点在 × 上 → 预览阶段立即关闭并吞掉事件(× 在激活标签页上曾被"防取消选中"逻辑吞掉,
        //   这正是"有时点 × 关不掉"的根因 —— 用户最常关的就是当前页);
        // · 点在激活标签其余区域 → 不允许 ToggleButton 取消选中,重新激活;
        // 隧道路由(外→内)保证本处理器先于内部按钮运行,顺序天然正确。
        session.TabButton.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && FindParent<Button>(source) == close)
            {
                e.Handled = true;
                _ = CloseTextTab(session);
                return;
            }
            if (session.TabButton.IsChecked == true)
            {
                e.Handled = true;
                _ = SwitchToSession(session);
            }
        };
        session.TabButton.Checked += (_, _) => _ = SwitchToSession(session);
        // 标签页右键:关闭此页 / 关闭其他 / 关闭全部(关闭全部后保留一个空白比较页)。
        // 菜单项用 Loc 键声明文案,随语言切换由 Loc.Apply 刷新。
        var menu = new ContextMenu();
        void TabItem(string key, Func<Task> action)
        {
            var item = new MenuItem();
            Loc.SetTarget(item, LocTarget.Header);
            Loc.SetKey(item, key);
            item.Click += (_, _) => _ = action();
            menu.Items.Add(item);
        }
        TabItem("tab_close_this", () => CloseTextTab(session));
        TabItem("tab_close_others", () => CloseOtherTabs(session));
        TabItem("tab_close_all", CloseAllTabs);
        session.TabButton.ContextMenu = menu;
        TabStripPanel.Children.Remove(NewTabButton);
        TabStripPanel.Children.Add(session.TabButton);
        TabStripPanel.Children.Add(NewTabButton);
        UpdateTabCaption(session);
    }

    /// <summary>Tab caption = title (with the unsaved marker); tooltip = the full pair of paths,
    /// which the caption may have trimmed.</summary>
    private static void UpdateTabCaption(TextSession session)
    {
        if (session.TabButton is null) return;
        session.Caption!.Text = session.Title;
        session.TabButton.ToolTip = session.IsBlank ? session.Title : session.LeftPath + "\n" + session.RightPath;
    }

    /// <summary>Language change: blank-tab titles come from the pack; the context-menu captions are
    /// refreshed by Loc.Apply.</summary>
    private void RefreshSessionTabTexts()
    {
        foreach (var session in _textSessions)
        {
            if (session.IsBlank) session.Title = ModularDiff.Gui.Controls.Lang.Get("tab_new_compare");
            UpdateTabCaption(session);
        }
    }

    private bool _tabCloseBusy;

    /// <summary>关闭一个文本标签页;返回是否真的关闭(取消/保存失败返回 false,批量关闭据此中止)。</summary>
    private async Task<bool> CloseTextTab(TextSession session)
    {
        if (_tabCloseBusy) return false;   // 连点 × 或与批量关闭并发时的重入保护
        _tabCloseBusy = true;
        try
        {
            if (ReferenceEquals(session, _activeSession)) SaveSessionState(session);
            // Unsaved byte edits live in the window-wide hex pane that belongs to this tab: closing
            // the tab would drop them, so ask first (text edits are covered by the prompt below).
            if (ReferenceEquals(_binarySession, session) && !_binaryIsPicture && Hex.IsDirty)
            {
                if (!await CanLeaveEdits()) return false;
                Hex.DiscardEdits();   // "discard" (after "save" the pane is no longer dirty and this is a no-op)
            }
            if (ReferenceEquals(_binarySession, session)) _binarySession = null;
            if (session.IsDirty)
            {
                var choice = MessageBox.Show(this, ModularDiff.Gui.Controls.Lang.Get("dlg_unsaved_tab", session.Title), ModularDiff.Gui.Controls.Lang.Get("dlg_unsaved_title"),
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
                if (choice == MessageBoxResult.Cancel) return false;
                if (choice == MessageBoxResult.Yes)
                {
                    _activeSession = session;
                    await RestoreSessionStateAsync(session);
                    // 保存失败必须中止关闭:SaveBuffers 返回 false 时弹窗已承诺"编辑仍保留",
                    // 无条件移除 session 会把唯一的缓冲区引用丢掉(数据丢失路径)。
                    if (!await SaveBuffers([session.LeftBuffer, session.RightBuffer])) return false;
                }
            }
            _textSessions.Remove(session);
            TabStripPanel.Children.Remove(session.TabButton);
            if (_textSessions.Count > 0) await SwitchToSession(_textSessions[^1]);
            else SwitchToFolderView();
            return true;
        }
        finally { _tabCloseBusy = false; }
    }

    /// <summary>关闭除指定标签外的全部文本标签;任一保存提示被取消则中止后续。</summary>
    private async Task CloseOtherTabs(TextSession keep)
    {
        foreach (var session in _textSessions.Where(s => !ReferenceEquals(s, keep)).ToArray())
            if (!await CloseTextTab(session)) return;
        if (_textSessions.Contains(keep) && !ReferenceEquals(_activeSession, keep)) await SwitchToSession(keep);
    }

    /// <summary>关闭全部文本标签,然后保留一个两侧空白的比较页(浏览器习惯)。</summary>
    private async Task CloseAllTabs()
    {
        foreach (var session in _textSessions.ToArray())
            if (!await CloseTextTab(session)) return;
        await CreateTextTab("", "");
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
                await RestoreSessionStateAsync(session); // folder chrome may have overwritten path boxes
            }
            return;
        }
        if (_activeSession is { } current) SaveSessionState(current);
        _activeSession = session;
        SetView(true);
        FolderTab.IsChecked = false;
        foreach (var other in _textSessions) other.TabButton!.IsChecked = ReferenceEquals(other, session);
        await RestoreSessionStateAsync(session);
        // Load content only when the session has no cached results (a freshly created tab whose
        // pair is already known; tabs with content just restore their state).
        if (session.FileResult is null && File.Exists(session.LeftPath) && File.Exists(session.RightPath))
            await LoadSessionContentAsync(session);
    }

    /// <summary>Content for a freshly created tab: while a folder comparison is loaded the pair
    /// opens as a text tab inside it (a root compare would wipe the Folder tab); otherwise the pair
    /// becomes the root comparison.</summary>
    private async Task LoadSessionContentAsync(TextSession session)
    {
        if (_folderResult is not null && _rootRequest is not null)
        {
            await LoadFilePairAsync(session.LeftPath, session.RightPath, PairCaption(session.LeftPath, session.RightPath), knownError: null, sourceNode: session.SourceNode);
            return;
        }
        LeftPath.Text = session.LeftPath; RightPath.Text = session.RightPath;
        await CompareAsync();
        SaveSessionState(session);
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

    /// <summary>Captures what is on screen into the tab's record. With <paramref name="adoptBoxes"/>
    /// the active tab follows path-box edits made by the user (the boxes are its own path bar); it is
    /// off when the capture happens because another pair is about to load — those boxes then describe
    /// the incoming pair, and letting the tab being left adopt them turned "open another pair" into
    /// "replace this tab" (the new pair was matched to it by path and loaded over its content).</summary>
    private void SaveSessionState(TextSession session, bool adoptBoxes = true)
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
        bool adopt = adoptBoxes && (blank || samePair || ReferenceEquals(session, _activeSession));
        if (adopt && File.Exists(LeftPath.Text)) session.LeftPath = LeftPath.Text;
        if (adopt && File.Exists(RightPath.Text)) session.RightPath = RightPath.Text;
        session.LeftTitle = LeftFileTitle.Text; session.LeftTitleTip = LeftFileTitle.ToolTip as string ?? "";
        session.RightTitle = RightFileTitle.Text; session.RightTitleTip = RightFileTitle.ToolTip as string ?? "";
        session.IsDirty = HasDirtyBuffers;
        session.Title = (session.IsDirty ? "● " : "") + session.Title.TrimStart('●', ' ');
        UpdateTabCaption(session);
    }

    /// <summary>Puts one tab back on screen: its panes, titles, paths and indicators. The hex and
    /// picture panes are window-wide, so a tab shows only its own view — a binary tab brings its pane
    /// back (reloaded, unless the pane still holds this tab's bytes, which is how unsaved hex edits
    /// survive a tab switch), a text tab hides both. A blank tab is blank on every side.</summary>
    private async Task RestoreSessionStateAsync(TextSession session)
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
        LeftFileTitle.Text = session.LeftTitle; LeftFileTitle.ToolTip = session.LeftTitleTip;
        RightFileTitle.Text = session.RightTitle; RightFileTitle.ToolTip = session.RightTitleTip;
        UpdateTextHeaders(_textFile);
        bool binary = _textFile is { IsBinary: true } && _fileResult is not null;
        if (!binary) { HexHost.Visibility = Visibility.Collapsed; PictureHost.Visibility = Visibility.Collapsed; }
        else if (ReferenceEquals(_binarySession, session))
            (_binaryIsPicture ? PictureHost : HexHost).Visibility = Visibility.Visible;
        else
        {
            if (PictureExtensions.Contains(Path.GetExtension(_fileResult!.LeftPath))) await ShowPictureAsync(_textFile!);
            else await ShowHexAsync(CancellationToken.None);
            BindBinaryView(session);
        }
        RefreshFileMessages(); RefreshHunkLabel();
        TextEmptyState.Visibility = session.IsBlank || (!binary && session.Presentation is not null && session.Presentation.Rows.Count == 0 && session.LeftBuffer is null)
            ? Visibility.Visible : Visibility.Collapsed;
        if (session.IsBlank) TextEmptyMessage.Text = ModularDiff.Gui.Controls.Lang.Get("blank_tab_hint");
        LeftPath.Text = session.LeftPath;
        RightPath.Text = session.RightPath;
        RememberCommittedPaths(session.LeftPath, session.RightPath);
        UpdateSummary(); UpdateEditState(); UpdateFileStats();
        if (session.IsBlank)
        {
            StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_blank_tab");
            HunkLabel.Text = "";
        }
        OpenButton.IsEnabled = false;
        PreviousFileButton.IsEnabled = NextFileButton.IsEnabled = _folderResult is not null;
        ExportButton.IsEnabled = _fileResult is not null;
    }

    private int InputMode()
    {
        bool hasLeft = File.Exists(LeftPath.Text) || Directory.Exists(LeftPath.Text);
        bool hasRight = File.Exists(RightPath.Text) || Directory.Exists(RightPath.Text);
        return (hasLeft ? 1 : 0) + (hasRight ? 1 : 0);
    }
}
