using System.IO;
using System.Windows;
using System.Windows.Controls;
using ModularDiff.Contracts;
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
        Add("撤销", pane.Buffer.CanUndo, () => HistoryRequested(pane, false));
        Add("重做", pane.Buffer.CanRedo, () => HistoryRequested(pane, true));
        menu.Items.Add(new Separator());
        Add("剪切", hasSelection, () => { SafeClipboard(() => Clipboard.SetText(pane.SelectedText())); pane.ReplaceSelection("", "Cut"); });
        Add("复制", hasSelection, () => SafeClipboard(() => Clipboard.SetText(pane.SelectedText())));
        Add("粘贴", Clipboard.ContainsText(), () => pane.ReplaceSelection(Clipboard.GetText(), "Paste"));
        Add("全部复制左→右", TextView.Visibility == Visibility.Visible && MergeRightButton.IsEnabled,
            () => _ = CopyAllBlocks(true));
        Add("全部复制右→左", TextView.Visibility == Visibility.Visible && MergeLeftButton.IsEnabled,
            () => _ = CopyAllBlocks(false));
        menu.Items.Add(new Separator());
        Add("复制差异块到" + (ReferenceEquals(pane, LeftCode) ? "右侧" : "左侧"),
            TextView.Visibility == Visibility.Visible && MergeRightButton.IsEnabled,
            () => _ = CopyBlock(ReferenceEquals(pane, LeftCode)));
        Add("复制选中行到" + (ReferenceEquals(pane, LeftCode) ? "右侧" : "左侧"),
            hasSelection && TextView.Visibility == Visibility.Visible,
            () => CopySelectionToSide(ReferenceEquals(pane, LeftCode)));
        Add("复制当前行到" + (ReferenceEquals(pane, LeftCode) ? "右侧" : "左侧"),
            TextView.Visibility == Visibility.Visible && pane.Buffer is not null,
            () => CopyLineToSide(ReferenceEquals(pane, LeftCode)));
        menu.Items.Add(new Separator());
        Add("修剪行尾空白", pane.Buffer is not null, () => TrimTrailingWhitespace(pane));
        Add("Tab → 空格", pane.Buffer is not null, () => ConvertTabsToSpaces(pane));
        Add("空格 → Tab（前导）", pane.Buffer is not null, () => ConvertSpacesToTabs(pane));
        Add("切换行尾风格", pane.Buffer is not null, () => ToggleLineEndings(pane));
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
                        Header = targets.Length > 1 ? $"换行 · {chosen.DisplayName}（{targets.Length} 行）" : "换行 · " + chosen.DisplayName,
                        ToolTip = "把所选行按各自领域规则拆成多行；行尾风格沿用原行",
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
            buffer.Apply(new TextEdit(first.Offset, last.Offset + last.Length - first.Offset, block.ToString(), $"换行 · {splitter.DisplayName}"));
            AfterProgrammaticEdit(pane, $"已按「{splitter.DisplayName}」换行 {applied} 行");
        }
        catch (Exception ex) { StatusLabel.Text = "换行失败：" + ex.Message; }
    }

    private void FormatSideClick(object sender, RoutedEventArgs e)
    {
        var pane = _activePane ?? LeftCode;
        if (pane.Buffer is null) { StatusLabel.Text = "此文件为只读，无法格式化。"; return; }
        var buffer = pane.Buffer;
        var formatter = _plugins.GetAll<ICodeFormatter>().FirstOrDefault(f => f.CanFormat(buffer.Path));
        if (formatter is null)
        {
            string available = string.Join("、", _plugins.GetAll<ICodeFormatter>().Select(f => f.DisplayName));
            StatusLabel.Text = $"没有支持 {Path.GetFileName(buffer.Path)} 的格式化插件{(available.Length > 0 ? $"（当前插件：{available}）" : "")}。";
            return;
        }
        try
        {
            string suffix = buffer.Text.Contains("\r\n") ? "\r" : "";   // 行尾风格沿用原文件
            var formatted = formatter.Format([.. DocumentLines.Parse(buffer.Text).Select(l => l.Text)]);
            buffer.Apply(new TextEdit(0, buffer.Text.Length, string.Join("\n" + suffix, formatted) + suffix, $"格式化 · {formatter.DisplayName}"));
            AfterProgrammaticEdit(pane, $"已按「{formatter.DisplayName}」格式化{(ReferenceEquals(pane, LeftCode) ? "左" : "右")}侧");
        }
        catch (Exception ex) { StatusLabel.Text = "格式化失败：" + ex.Message; }
    }

    private async void RealignClick(object sender, RoutedEventArgs e)
    {
        if (_leftBuffer is null || _rightBuffer is null) return;
        _editTask = RecompareBuffers(false);
        await _editTask;
        if (StatusLabel.Text.Length == 0 || !StatusLabel.Text.Contains("失败")) StatusLabel.Text = "已按当前算法与忽略选项重新对齐。";
    }

    private void AutoRealignChanged(object sender, RoutedEventArgs e)
    {
        _autoRealign = AutoRealignToggle.IsChecked == true;
        if (!_autoRealign && HasDirtyBuffers) HunkLabel.Text = "未对齐 · 点 ⟳ 重算";
    }

    /// <summary>Shared tail for edits issued from line tools: refresh the draft view and
    /// realign immediately when ⚡ is on, otherwise wait for the manual ⟳.</summary>
    private void AfterProgrammaticEdit(DiffPane pane, string message)
    {
        _activePane = pane;
        ShowDraft(); UpdateEditState();
        if (_autoRealign) _editTask = RecompareBuffers(false);
        else { HunkLabel.Text = "未对齐 · 点 ⟳ 重算"; StatusLabel.Text = message; }
    }
}
