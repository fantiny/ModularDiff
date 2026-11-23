using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ModularDiff.Gui.Controls;

namespace ModularDiff.Gui;

/// <summary>Smoke coverage for localization, the path bar and tab chrome. These checks drive the
/// production entry points and then read the real element tree, so a caption that is left behind in
/// the previous language — the way several labels stayed Chinese in English mode — fails the run.</summary>
public partial class MainWindow
{
    private static bool HasChinese(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (char c in text)
            if (c is >= '\u4e00' and <= '\u9fff' or >= '\u3000' and <= '\u303f' or >= '\uff00' and <= '\uffef') return true;
        return false;
    }

    /// <summary>Every caption of this window's chrome: texts, contents, tooltips, menu and column
    /// headers, the title. Elements that show data rather than captions are left out (paths, file
    /// titles, status text, query boxes, session lists, tab captions) — a Chinese file name is data,
    /// not a localization gap.</summary>
    private IEnumerable<(string Where, string Text)> ChromeTexts()
    {
        var data = new HashSet<object> { StatusLabel, HunkLabel, LeftFileTitle, RightFileTitle, LeftPath, RightPath, FindBox, ReplaceBox, FolderSearch, UnimportantBox, SessionBox, RecentBox };
        var pending = new Stack<DependencyObject>();
        pending.Push(this);
        while (pending.TryPop(out var current))
        {
            static string Name(DependencyObject element) => (element as FrameworkElement)?.Name is { Length: > 0 } name ? name : element.GetType().Name;
            if (current is Window window) yield return ("Window.Title", window.Title);
            if (current is TextBlock { Text.Length: > 0 } text && !data.Contains(text) && text.Parent is not DockPanel { Parent: ToggleButton })
                yield return (Name(text), text.Text);
            if (current is ContentControl { Content: string content } control && !data.Contains(control))
                yield return (Name(control), content);
            if (current is HeaderedItemsControl { Header: string header }) yield return (Name(current), header);
            if (current is DataGridColumn { Header: string columnHeader }) yield return (Name(current), columnHeader);
            if (current is FrameworkElement element && !data.Contains(element))
            {
                if (element.ToolTip is string tip) yield return (Name(element) + ".ToolTip", tip);
                else if (element.ToolTip is ToolTip { Content: string toolTipContent }) yield return (Name(element) + ".ToolTip", toolTipContent);
                if (element.ContextMenu is { } menu) pending.Push(menu);
            }
            if (current is DataGrid grid)
                foreach (var column in grid.Columns) pending.Push(column);
            if (current is Popup { Child: DependencyObject child }) pending.Push(child);
            if (current is FrameworkElement or FrameworkContentElement)
                foreach (object item in LogicalTreeHelper.GetChildren(current))
                    if (item is DependencyObject dependency) pending.Push(dependency);
        }
    }

    /// <summary>Switches the UI language through the pack engine (not persisted: a test must not
    /// change the language the user remembered), then verifies the whole chrome followed — labels
    /// that used to be missed, stale derived labels, and plugin strings through Lang.Text.</summary>
    private async Task LocalizationSmokeAsync(string? screenshot)
    {
        string original = Controls.Lang.CurrentName;
        try
        {
            foreach (string language in Controls.Lang.Available)
            {
                Controls.Lang.Apply(language);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                string code = Controls.Lang.CurrentCode;
                if (!string.Equals(LangBox.SelectedItem as string, language, StringComparison.Ordinal))
                    throw new InvalidOperationException($"The language dropdown did not follow the switch to '{language}'.");
                if (code.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                {
                    var english = ChromeTexts().Select(t => t.Text).ToList();
                    var leftovers = ChromeTexts().Where(t => HasChinese(t.Text)).ToList();
                    if (leftovers.Count > 0)
                        throw new InvalidOperationException("English mode still shows Chinese chrome: "
                            + string.Join(" | ", leftovers.Take(8).Select(o => $"{o.Where}='{o.Text}'")));
                    // Captions that were hard-coded in XAML before (and so never followed a switch).
                    foreach (string caption in new[] { "Save left", "Save right", "Show", "Format", "Realign", "Auto", "Wrap", "Layout", "Replace", "Replace all", "Status", "Name", "Modified", "Size" })
                        if (!english.Contains(caption)) throw new InvalidOperationException($"English chrome is missing the caption '{caption}'.");
                    if (!english.Any(t => t.EndsWith("plugins loaded", StringComparison.Ordinal)))
                        throw new InvalidOperationException("The plugin counter did not follow the switch to English.");
                    // The blank tab this smoke runs on is blank on every side: no stale title, status or hint of the previous tab.
                    if (_activeSession is { IsBlank: true }
                        && (StatusLabel.Text != Controls.Lang.Get("status_blank_tab") || LeftFileTitle.Text.Length > 0 || RightFileTitle.Text.Length > 0
                            || TextEmptyMessage.Text != Controls.Lang.Get("blank_tab_hint")))
                        throw new InvalidOperationException($"A blank tab must be blank on every side; status='{StatusLabel.Text}', title='{LeftFileTitle.Text}', hint='{TextEmptyMessage.Text}'.");
                }
                else if (code.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase))
                {
                    var traditional = ChromeTexts().Select(t => t.Text).ToList();
                    // Simplified-only forms prove the pack was applied rather than left at zh-CN.
                    if (!traditional.Contains("儲存左") || traditional.Contains("保存左") || traditional.Contains("Show"))
                        throw new InvalidOperationException("Traditional Chinese chrome is incomplete or stale.");
                }
                else if (code.StartsWith("zh-CN", StringComparison.OrdinalIgnoreCase))
                {
                    var simplified = ChromeTexts().Select(t => t.Text).ToList();
                    if (!simplified.Contains("保存左") || !simplified.Contains("显示") || simplified.Contains("Show") || simplified.Contains("Save left"))
                        throw new InvalidOperationException("Simplified Chinese chrome is incomplete or stale after switching back.");
                    if (!simplified.Any(t => t.EndsWith("个插件已加载", StringComparison.Ordinal)))
                        throw new InvalidOperationException("The plugin counter did not follow the switch back to Chinese.");
                }
                if (screenshot is not null && (code.StartsWith("en") || code.StartsWith("zh-CN")))
                {
                    string directory = Path.GetDirectoryName(screenshot)!;
                    string stem = Path.GetFileNameWithoutExtension(screenshot).Replace(".text", "");
                    SaveScreenshot(Path.Combine(directory, $"{stem}.{code.ToLowerInvariant()}.png"));
                }
            }
            // Strings that originate in Core/plugins follow the UI language as well (exact, templated
            // and composed messages); the path-box hint is translated beneath the path itself.
            Controls.Lang.Apply("English");
            string pluginMessages = PluginMessageExpectations();
            if (pluginMessages.Length > 0) throw new InvalidOperationException("Plugin messages did not follow the language: " + pluginMessages);
            // An earlier "invalid path" check of this smoke left its error tooltip open on the left box
            // (it closes by itself after a few seconds); finish it now so the regular hint is back.
            if (LeftPath.ToolTip is ToolTip { IsOpen: true } stale) { stale.IsOpen = false; UpdatePathBoxTips(); }
            string pathHint = (LeftPath.ToolTip as string) ?? $"<{LeftPath.ToolTip?.GetType().Name ?? "null"}>";
            if (!pathHint.EndsWith(Controls.Lang.Get("path_tip"), StringComparison.Ordinal))
                throw new InvalidOperationException($"The path-box hint did not follow the language; got: '{pathHint}'.");
            // The path box behaves like an address bar: its context menu is localized …
            var menuCaptions = (LeftPath.ContextMenu?.Items.OfType<MenuItem>() ?? []).Select(item => item.Header as string ?? "").ToList();
            foreach (string caption in new[] { "Undo", "Cut", "Copy", "Paste", "Delete", "Select all" })
                if (!menuCaptions.Contains(caption)) throw new InvalidOperationException($"The path-box context menu is missing '{caption}' (has: {string.Join(", ", menuCaptions)}).");
            // … and one click into it selects the whole path, ready to copy.
            RightCode.Focus();
            if (LeftPath.IsKeyboardFocusWithin) throw new InvalidOperationException("Smoke precondition: the path box must not have focus before the click.");
            var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
            LeftPath.RaiseEvent(click);
            if (!click.Handled || LeftPath.SelectionStart != 0 || LeftPath.SelectionLength != LeftPath.Text.Length)
                throw new InvalidOperationException($"A click into the path box must select the whole path (start {LeftPath.SelectionStart}, length {LeftPath.SelectionLength} of {LeftPath.Text.Length}).");
        }
        finally { Controls.Lang.Apply(original); }
    }

    /// <summary>English renderings of the source strings below, exactly as Core/plugins emit them.</summary>
    private static string PluginMessageExpectations()
    {
        var problems = new List<string>();
        void Same(string source, string expected)
        {
            string actual = Controls.Lang.Text(source);
            if (!string.Equals(actual, expected, StringComparison.Ordinal)) problems.Add($"'{source}' → '{actual}' (expected '{expected}')");
        }
        Same("修剪行尾空白", "Trim trailing whitespace");                                   // context-menu label from a plugin
        Same("CSS 规范格式化", "CSS standard formatting");                                  // plugin display name
        Same("左侧路径不存在：C:\\nope", "The left path does not exist: C:\\nope");          // templated message with a path
        Same("操作记录写入失败，已停止后续文件：同步记录库正在使用或无法访问，请稍后重试。；请在历史中检查准备记录。",
            "Journal write failed, remaining files were not processed: The sync journal is in use or cannot be accessed; try again later. "
            + "Check the Prepared records in the history.");                                // composed: template around another message
        return string.Join("; ", problems);
    }

    /// <summary>Path bar as a real text box: the pair typed into it opens as a text tab while the
    /// folder comparison survives; a wrong path is flagged at the box and the committed pair comes
    /// back; long file names never widen a tab past the shared maximum.</summary>
    private async Task PathBarAndTabChromeSmokeAsync()
    {
        if (_folderResult is null)
        {
            // This smoke can start on a pair of files, with no folder comparison loaded. Then a pair
            // typed into the path bar is the root comparison, shown in the active blank tab; and two
            // folders typed the same way start a folder comparison.
            await CloseAllTabs();
            LeftPath.Text = Path.GetFullPath(Arg("--left")!); RightPath.Text = Path.GetFullPath(Arg("--right")!);
            await CommitPathBarAsync();
            if (_textSessions.Count != 1 || _activeSession is not { IsBlank: false } || _fileResult is null || TextView.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Without a folder comparison, a file pair typed into the path bar must load into the active blank tab.");
            string samples = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Arg("--left")!))!, ".."));
            LeftPath.Text = Path.Combine(samples, "left"); RightPath.Text = Path.Combine(samples, "right");
            await CommitPathBarAsync();
            if (_folderResult is null || FolderView.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Two folders typed into the path bar must start a folder comparison.");
        }
        await CloseAllTabs();   // leaves one active blank tab, as after "close all tabs" in the UI
        if (_folderResult is null || _tree is null) throw new InvalidOperationException("Smoke precondition: a loaded folder comparison.");
        var file = _tree.All.First(n => !n.IsDirectory && n.LeftExists && n.RightExists);   // present on both sides
        string left = Path.Combine(_folderResult.LeftPath, file.RelativePath), right = Path.Combine(_folderResult.RightPath, file.RelativePath);
        int tabs = _textSessions.Count;

        // Edit both boxes to another pair and press Enter: the active blank tab is adopted (no
        // extra tab) and the Folder tab keeps its comparison. A root compare used to wipe it.
        LeftPath.Text = left; RightPath.Text = right;
        await CommitPathBarAsync();
        if (_textSessions.Count != tabs || _activeSession is not { IsBlank: false } || _textFile is null || TextView.Visibility != Visibility.Visible)
            throw new InvalidOperationException($"A file pair typed into the path bar must load into the active blank tab (tabs {_textSessions.Count} → expected {tabs}, "
                + $"blank={_activeSession?.IsBlank}, file={_textFile is not null}, textView={TextView.Visibility}, status='{StatusLabel.Text}').");
        if (_folderResult is null || _tree is null || LeftFolders.ItemsSource is null)
            throw new InvalidOperationException("Comparing a file pair from the path bar must not wipe the folder comparison.");

        // A wrong path: the reason is shown at the offending box and the committed pair is restored.
        string shownLeft = LeftPath.Text;
        LeftPath.Text = Path.Combine(Path.GetDirectoryName(left)!, "__no_such_file__.txt");
        await CommitPathBarAsync();
        if (!string.Equals(LeftPath.Text, shownLeft, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("An invalid path must restore the committed pair in the path boxes.");
        if (LeftPath.ToolTip is not ToolTip { IsOpen: true, Content: string reason } || !reason.Contains("__no_such_file__", StringComparison.Ordinal))
            throw new InvalidOperationException("An invalid path must be flagged by an open tooltip on the offending box.");

        // The Folder tab still lists its rows and restores the directory roots.
        FolderTab.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (LeftFolders.Items.Count == 0 || !string.Equals(LeftPath.Text, _folderResult.LeftPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Folder tab lost its comparison after a path-bar file compare.");

        // Long file names: every tab has the same maximum width; the caption trims, the × stays inside.
        string directory = Path.Combine(Path.GetTempPath(), "ModularDiff-longtab-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string longLeft = Path.Combine(directory, new string('x', 150) + ".txt"), longRight = Path.Combine(directory, new string('y', 150) + ".txt");
            await File.WriteAllTextAsync(longLeft, "one\ntwo\n"); await File.WriteAllTextAsync(longRight, "one\nTWO\n");
            LeftPath.Text = longLeft; RightPath.Text = longRight;
            await CommitPathBarAsync();
            var tab = _activeSession?.TabButton ?? throw new InvalidOperationException("The long-name pair did not open in a tab.");
            UpdateLayout();
            if (tab.ActualWidth > tab.MaxWidth + 0.5)
                throw new InvalidOperationException($"Tab is wider than the shared maximum ({tab.ActualWidth:0} > {tab.MaxWidth:0}).");
            var panel = tab.Content as DockPanel ?? throw new InvalidOperationException("Tab caption layout changed.");
            var caption = panel.Children.OfType<TextBlock>().Single(); var close = panel.Children.OfType<Button>().Single();
            if (caption.TextTrimming != TextTrimming.CharacterEllipsis)
                throw new InvalidOperationException("The tab caption must trim with an ellipsis.");
            if (close.TranslatePoint(new Point(close.ActualWidth, 0), tab).X > tab.ActualWidth + 0.5)
                throw new InvalidOperationException("A long tab caption pushed the close button out of the tab.");
            if (!(tab.ToolTip as string ?? "").Contains(longLeft, StringComparison.Ordinal))
                throw new InvalidOperationException("The tab tooltip must show the full path of a trimmed caption.");
        }
        finally { try { Directory.Delete(directory, true); } catch { /* locked temp files go away with the process */ } }
        await BinaryTabSwitchSmokeAsync();
        await CloseAllTabs();
        FolderTab.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    /// <summary>A hex tab and a text tab side by side: whichever is active shows its own view. The
    /// hex/picture panes are window-wide, so a tab switch has to hide the other kind and bring the
    /// right one back (they used to stay on screen over a text tab, and vanish from a hex tab).</summary>
    private async Task BinaryTabSwitchSmokeAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ModularDiff-bintabs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            byte[] a = { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0xFF, 0xFE, 0x1A }, b = { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0xFF, 0xFE, 0x7B };   // NUL byte → binary
            string binLeft = Path.Combine(directory, "a.bin"), binRight = Path.Combine(directory, "b.bin");
            string textLeft = Path.Combine(directory, "t.txt"), textRight = Path.Combine(directory, "u.txt");
            File.WriteAllBytes(binLeft, a); File.WriteAllBytes(binRight, b);
            await File.WriteAllTextAsync(textLeft, "one\ntwo\n"); await File.WriteAllTextAsync(textRight, "one\nTWO\n");

            LeftPath.Text = binLeft; RightPath.Text = binRight;
            await CommitPathBarAsync();
            var hexTab = _activeSession ?? throw new InvalidOperationException("The binary pair did not open in a tab.");
            if (HexHost.Visibility != Visibility.Visible) throw new InvalidOperationException("The binary pair must show the hex view.");

            LeftPath.Text = textLeft; RightPath.Text = textRight;
            await CommitPathBarAsync();
            var textTab = _activeSession ?? throw new InvalidOperationException("The text pair did not open in a tab.");
            if (HexHost.Visibility != Visibility.Collapsed || LeftCode.Rows.Count == 0)
                throw new InvalidOperationException("A text tab must not be covered by the hex view of another tab.");

            await SwitchToSession(hexTab);
            if (HexHost.Visibility != Visibility.Visible)
                throw new InvalidOperationException($"Switching back to the hex tab must show the hex view again (bound={ReferenceEquals(_binarySession, hexTab)}, picture={_binaryIsPicture}, "
                    + $"binary={_textFile?.IsBinary}, result={_fileResult is not null}, picturePane={PictureHost.Visibility}).");
            await SwitchToSession(textTab);
            if (HexHost.Visibility != Visibility.Collapsed || LeftCode.Rows.Count == 0)
                throw new InvalidOperationException("Switching back to the text tab must hide the hex view.");
            // The titles above the panes belong to the active tab as well.
            if (!string.Equals(LeftFileTitle.Text, textLeft, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The file title belongs to another tab: '{LeftFileTitle.Text}'.");
        }
        finally { try { Directory.Delete(directory, true); } catch { /* locked temp files go away with the process */ } }
    }
}
