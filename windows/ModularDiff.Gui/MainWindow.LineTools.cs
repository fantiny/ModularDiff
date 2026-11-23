using System.IO;
using System.Windows;
using System.Windows.Controls;
using ModularDiff.Contracts;
using ModularDiff.Core;
using ModularDiff.Gui.Controls;

namespace ModularDiff.Gui;

/// <summary>Line-tool integration: ILineSplitter context menu, ICodeFormatter side formatting,
/// and manual/auto realignment on top of the editing pipeline.</summary>
public partial class MainWindow
{
    private bool _autoRealign = true;

    private void InitializeLineTools()
    {
        LeftCode.SplitMenuRequested += SplitMenuRequested;
        RightCode.SplitMenuRequested += SplitMenuRequested;
        AutoRealignToggle.IsChecked = true;
    }

    private void SplitMenuRequested(DiffPane pane, int rowIndex)
    {
        if (pane.Buffer is null) return;
        var menu = new ContextMenu();
        void Add(string header, bool enabled, Action action)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        bool hasSelection = pane.SelectedText().Length > 0;
        bool toRight = ReferenceEquals(pane, LeftCode);
        Add(Lang.Get("menu_undo"), pane.Buffer.CanUndo, () => HistoryRequested(pane, false));
        Add(Lang.Get("menu_redo"), pane.Buffer.CanRedo, () => HistoryRequested(pane, true));
        menu.Items.Add(new Separator());
        Add(Lang.Get("menu_cut"), hasSelection, () => { SafeClipboard(() => Clipboard.SetText(pane.SelectedText())); pane.ReplaceSelection("", "Cut"); });
        Add(Lang.Get("menu_copy"), hasSelection, () => SafeClipboard(() => Clipboard.SetText(pane.SelectedText())));
        Add(Lang.Get("menu_paste"), Clipboard.ContainsText(), () => pane.ReplaceSelection(Clipboard.GetText(), "Paste"));
        Add(Lang.Get("menu_copy_all_right"), TextView.Visibility == Visibility.Visible && MergeRightButton.IsEnabled,
            () => _ = CopyAllBlocks(true));
        Add(Lang.Get("menu_copy_all_left"), TextView.Visibility == Visibility.Visible && MergeLeftButton.IsEnabled,
            () => _ = CopyAllBlocks(false));
        menu.Items.Add(new Separator());
        Add(Lang.Get(toRight ? "menu_copy_diff_right" : "menu_copy_diff_left"),
            TextView.Visibility == Visibility.Visible && MergeRightButton.IsEnabled,
            () => _ = CopyBlock(toRight));
        Add(Lang.Get(toRight ? "menu_copy_selection_right" : "menu_copy_selection_left"),
            hasSelection && TextView.Visibility == Visibility.Visible,
            () => CopySelectionToSide(toRight));
        Add(Lang.Get(toRight ? "menu_copy_line_right" : "menu_copy_line_left"),
            TextView.Visibility == Visibility.Visible && pane.Buffer is not null,
            () => CopyLineToSide(toRight));
        menu.Items.Add(new Separator());
        // File transforms come exclusively from IFileTypeProvider plugins (GenericFileTools covers all types).
        // Manual alignment (Beyond Compare's Align With / Isolate)
        if (TextView.Visibility == Visibility.Visible && pane.Buffer is not null)
        {
            menu.Items.Add(new Separator());
            Add(Lang.Get("menu_align_with"), hasSelection, () => StartAlignWith(pane));
            Add(Lang.Get("menu_isolate"), hasSelection, () => IsolateSelection(pane));
            Add(Lang.Get("menu_clear_align"), _alignmentHints.Count > 0 || _isolatedLeft.Count > 0 || _isolatedRight.Count > 0,
                () => ClearAlignmentHints());
        }
        // File-type-specific actions from IFileTypeProvider plugins (JSON format, C# comment, XML pretty, etc.)
        // Providers with "*" in Extensions are generic (match any file type).
        if (pane.Buffer is not null)
        {
            string extension = System.IO.Path.GetExtension(pane.Buffer.Path);
            var specific = _plugins.GetAll<IFileTypeProvider>()
                .Where(p => p.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                .ToList();
            var generic = _plugins.GetAll<IFileTypeProvider>()
                .Where(p => p.Extensions.Contains("*"))
                .ToList();
            var providers = specific.Concat(generic).ToArray();
            if (providers.Length > 0)
            {
                menu.Items.Add(new Separator());
                foreach (var provider in providers)
                foreach (var (label, shortcut, transform) in provider.EditActions)
                {
                    var action = transform;
                    var capturedPane = pane;
                    string caption = Lang.Text(label) + (shortcut is not null ? $" ({shortcut})" : "");   // plugin labels follow the UI language
                    Add(caption, true, () =>
                    {
                        string newText = action(new EditContext(capturedPane.Buffer!, capturedPane.SelectedRow, 0));
                        if (newText.Length > 0 && newText != capturedPane.Buffer!.Text)
                            capturedPane.Buffer.Apply(new Contracts.TextEdit(0, capturedPane.Buffer.Text.Length, newText, caption));
                        AfterProgrammaticEdit(capturedPane, caption);
                    });
                }
            }
        }
        int line = pane.RowSourceLine(rowIndex);
        string current = pane.Line(line)?.Text ?? "";
        if (line >= 0 && current.Length > 0)
        {
            string path = pane.Buffer!.Path;
            var splitters = _plugins.GetAll<ILineSplitter>().Where(s => s.CanSplit(path, current)).ToArray();
            if (splitters.Length > 0)
            {
                var candidates = new HashSet<int>(pane.SelectedSourceLines()) { line };
                menu.Items.Add(new Separator());
                foreach (var splitter in splitters)
                {
                    var chosen = splitter;
                    int[] targets = [.. candidates.Where(n => n == line || chosen.CanSplit(path, pane.Line(n)?.Text ?? "")).Order()];
                    var item = new MenuItem
                    {
                        Header = targets.Length > 1
                            ? Lang.Get("menu_split_many", Lang.Text(chosen.DisplayName), targets.Length)
                            : Lang.Get("menu_split_one", Lang.Text(chosen.DisplayName)),
                        ToolTip = Lang.Get("menu_split_tip"),
                    };
                    item.Click += (_, _) => ApplySplit(pane, chosen, targets);
                    menu.Items.Add(item);
                }
            }
        }
        menu.PlacementTarget = pane;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private static void SafeClipboard(Action action)
    {
        try { action(); } catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy: ignore */ }
    }

    /// <summary>Replaces the covered block in one edit so a single undo restores it;
    /// per-line endings are preserved, and split lines inherit the original line's ending.</summary>
    private void ApplySplit(DiffPane pane, ILineSplitter splitter, int[] targets)
    {
        if (pane.Buffer is null || targets.Length == 0) return;
        try
        {
            var buffer = pane.Buffer;
            var first = pane.Line(targets[0]);
            var last = pane.Line(targets[^1]);
            if (first is null || last is null) return;
            int applied = 0;
            var block = new System.Text.StringBuilder();
            for (int n = targets[0]; n <= targets[^1]; n++)
            {
                var line = pane.Line(n);
                if (line is null) return;
                string body = line.Text;
                if (Array.IndexOf(targets, n) >= 0 && splitter.CanSplit(buffer.Path, line.Text))
                {
                    var parts = splitter.Split(line.Text);
                    if (parts.Count >= 2) { body = string.Join(line.Ending.Length > 0 ? line.Ending : "\n", parts); applied++; }
                }
                block.Append(body);
                if (n < targets[^1]) block.Append(line.Ending.Length > 0 ? line.Ending : "\n");
                else block.Append(last.Ending);   // the block's final ending matches the replaced range exactly
            }
            if (applied == 0) return;
            buffer.Apply(new TextEdit(first.Offset, last.Offset + last.Length - first.Offset, block.ToString(), Lang.Get("edit_desc_split", Lang.Text(splitter.DisplayName))));
            AfterProgrammaticEdit(pane, Lang.Get("status_split_done", Lang.Text(splitter.DisplayName), applied));
        }
        catch (Exception ex) { StatusLabel.Text = Lang.Get("status_split_failed", ModularDiff.Gui.Controls.Lang.Text(ex.Message)); }
    }

    private void FormatSideClick(object sender, RoutedEventArgs e)
    {
        var pane = _activePane ?? LeftCode;
        if (pane.Buffer is null) { StatusLabel.Text = Lang.Get("status_readonly_format"); return; }
        var buffer = pane.Buffer;
        var formatter = _plugins.GetAll<ICodeFormatter>().FirstOrDefault(f => f.CanFormat(buffer.Path));
        if (formatter is null)
        {
            string available = string.Join(", ", _plugins.GetAll<ICodeFormatter>().Select(f => Lang.Text(f.DisplayName)));
            StatusLabel.Text = Lang.Get("status_no_formatter", Path.GetFileName(buffer.Path), available.Length > 0 ? Lang.Get("status_formatter_list", available) : "");
            return;
        }
        try
        {
            var formatted = formatter.Format([.. DocumentLines.Parse(buffer.Text).Select(l => l.Text)]);
            // 行尾风格/结尾换行沿原文 —— 走 Core 助手;此前手写 join 把 CRLF 拼成 LFCR(审查 K3)。
            buffer.Apply(new TextEdit(0, buffer.Text.Length, LineText.Rebuild(formatted, buffer.Text), Lang.Get("edit_desc_format", Lang.Text(formatter.DisplayName))));
            AfterProgrammaticEdit(pane, Lang.Get("status_format_done", Lang.Text(formatter.DisplayName), Lang.Get(ReferenceEquals(pane, LeftCode) ? "side_left" : "side_right")));
        }
        catch (Exception ex) { StatusLabel.Text = Lang.Get("status_format_failed", ModularDiff.Gui.Controls.Lang.Text(ex.Message)); }
    }

    private async void RealignClick(object sender, RoutedEventArgs e)
    {
        if (_leftBuffer is null || _rightBuffer is null) return;
        _editTask = RecompareBuffers(false);
        await _editTask;
        string failToken = Lang.Get("fail_token");
        bool failed = StatusLabel.Text.Contains(failToken, StringComparison.Ordinal)
            || StatusLabel.Text.Contains("fail", StringComparison.OrdinalIgnoreCase);
        if (StatusLabel.Text.Length == 0 || !failed)
            StatusLabel.Text = Lang.Get("status_realigned");
    }

    private void AutoRealignChanged(object sender, RoutedEventArgs e)
    {
        _autoRealign = AutoRealignToggle.IsChecked == true;
        if (!_autoRealign && HasDirtyBuffers) HunkLabel.Text = ModularDiff.Gui.Controls.Lang.Get("hunks_unaligned");
    }

    /// <summary>Shared tail for edits issued from line tools: refresh the draft view and
    /// realign immediately when ⚡ is on, otherwise wait for the manual ⟳.</summary>
    private void AfterProgrammaticEdit(DiffPane pane, string message)
    {
        _activePane = pane;
        ShowDraft(); UpdateEditState();
        if (_autoRealign) _editTask = RecompareBuffers(false);
        else { HunkLabel.Text = ModularDiff.Gui.Controls.Lang.Get("hunks_unaligned"); StatusLabel.Text = message; }
    }
}
