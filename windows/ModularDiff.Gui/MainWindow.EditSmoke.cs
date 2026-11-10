using System.IO;
using System.Windows;
using System.Windows.Threading;
using ModularDiff.Contracts;

namespace ModularDiff.Gui;

public partial class MainWindow
{
    private async void EditSmokeTest(object sender, RoutedEventArgs e)
    {
        int exit = 0;
        // This mode always creates its own files, never modifies --left/--right inputs.
        string root = Path.Combine(AppContext.BaseDirectory, "edit-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            LeftPath.Text = Path.Combine(root, "left.cs"); RightPath.Text = Path.Combine(root, "right.cs");
            string left = "// Sample\r\nclass Example\r\n{\r\n    int value = 1;\r\n}\r\n";
            string right = left.Replace("value = 1", "value = 2");
            await File.WriteAllTextAsync(LeftPath.Text, left); await File.WriteAllTextAsync(RightPath.Text, right);
            await CompareAsync();
            Require(_leftBuffer is not null && _rightBuffer is not null, "Editable buffers missing");
            LeftCode.Focus(); LeftCode.SetCaret(0); LeftCode.ReplaceSelection("// 左侧编辑\r\n"); await _editTask;
            RightCode.Focus(); RightCode.SetCaret(0); RightCode.ReplaceSelection("// 右侧编辑\r\n"); await _editTask;
            Require(HasDirtyBuffers && SaveAllButton.IsEnabled && UndoButton.IsEnabled, "Dirty toolbar state");
            string changedRight = _rightBuffer!.Text;
            HistoryRequested(RightCode, false); await _editTask; Require(_rightBuffer.Text == right, "Right undo");
            HistoryRequested(RightCode, true); await _editTask; Require(_rightBuffer.Text == changedRight, "Right redo");
            _currentHunk = -1; NavigateHunk(1); Require(MergeRightButton.IsEnabled, "Merge action disabled");
            await CopyBlock(true); Require(_rightBuffer.Text.StartsWith("// 左侧编辑"), "Left to right merge");
            HistoryRequested(RightCode, false); await _editTask; Require(_rightBuffer.Text == changedRight, "Block undo");
            _currentHunk = -1; NavigateHunk(1); await CopyBlock(false);
            Require(_leftBuffer!.Text.StartsWith("// 右侧编辑"), "Right to left merge");
            Require(await SaveBuffers([_leftBuffer, _rightBuffer]) && !HasDirtyBuffers, "Save both");
            Require(await File.ReadAllTextAsync(LeftPath.Text) == _leftBuffer.Text && await File.ReadAllTextAsync(RightPath.Text) == _rightBuffer.Text, "Disk contents");
            HistoryRequested(LeftCode, false); await _editTask; Require(_leftBuffer.IsDirty, "Undo after save");
            HistoryRequested(LeftCode, true); await _editTask; Require(!_leftBuffer.IsDirty, "Redo to save checkpoint");
            // Direct editing of an empty buffer, including the virtual EOF caret row.
            LeftCode.SetCaret(0); LeftCode.SetCaret(_leftBuffer.Text.Length, extend: true); LeftCode.ReplaceSelection(""); await _editTask;
            Require(_leftBuffer.Text == "" && TextEmptyState.Visibility == Visibility.Collapsed, "Empty buffer must remain editable");
            LeftCode.ReplaceSelection("// empty-buffer input\r\n"); await _editTask;
            HistoryRequested(LeftCode, false); await _editTask; HistoryRequested(LeftCode, false); await _editTask;
            Require(!_leftBuffer.IsDirty, "Restore after empty edit");

            // Line tools: css-format formatting, css declaration splitting, and ⟳/⚡ realignment.
            string cssLeft = "a { color: red; background: blue }\nb { margin: 0 }\n";
            string cssRight = "a { color: blue; background: blue }\nb { margin: 0 }\n";
            LeftPath.Text = Path.Combine(root, "left.css"); RightPath.Text = Path.Combine(root, "right.css");
            await File.WriteAllTextAsync(LeftPath.Text, cssLeft); await File.WriteAllTextAsync(RightPath.Text, cssRight);
            await CompareAsync();
            Require(_leftBuffer is not null && _rightBuffer is not null, "CSS buffers missing");
            _activePane = LeftCode;
            FormatSideClick(this, new RoutedEventArgs()); await _editTask;
            Require(_leftBuffer!.Text != cssLeft && _leftBuffer.Text.Split('\n').Length > 3, "css-format output");
            HistoryRequested(LeftCode, false); await _editTask;
            Require(_leftBuffer.Text == cssLeft, "Format undo must restore in one step");
            var splitter = _plugins.GetAll<ILineSplitter>().FirstOrDefault(s => s.CanSplit(LeftPath.Text, "a { color: red; background: blue }"))
                ?? throw new InvalidOperationException("css splitter missing");
            // Multi-line selection (as produced by number-column Shift clicks) feeds the batch range.
            var firstLine = RightCode.Line(1) ?? throw new InvalidOperationException("right line 1");
            var secondLine = RightCode.Line(2) ?? throw new InvalidOperationException("right line 2");
            RightCode.SetCaret(firstLine.Offset);
            RightCode.SetCaret(secondLine.Offset + secondLine.Length, extend: true);
            Require(RightCode.SelectedSourceLines().SequenceEqual(new[] { 1, 2 }), "Selection range lines");
            ApplySplit(RightCode, splitter, [1]); await _editTask;
            Require(_rightBuffer!.Text.Split('\n').Length > 3 && _rightBuffer.Text.Contains("color: blue"), "Split output");
            HistoryRequested(RightCode, false); await _editTask;
            Require(_rightBuffer.Text == cssRight, "Split undo must restore in one step");
            AutoRealignToggle.IsChecked = false;
            ApplySplit(RightCode, splitter, [1]);
            Require(HunkLabel.Text.Contains("未对齐"), "Manual realign pending hint");
            string splitDraft = _rightBuffer.Text;
            RealignClick(this, new RoutedEventArgs()); await _editTask;
            Require(_presentation is not null && _presentation.Rows.Count > 3, "Manual realign recomputes");
            Require(_rightBuffer.Text == splitDraft, "Realign must not touch the edit");
            AutoRealignToggle.IsChecked = true;
            HistoryRequested(RightCode, false); await _editTask;
            Require(_rightBuffer.Text == cssRight, "Cleanup undo");

            // Find & replace on the focused side; each operation is one edit so a single undo restores.
            FindBox.Text = "blue"; ReplaceBox.Text = "green"; _activePane = RightCode;
            await ReplaceNext();
            Require(_rightBuffer.Text.Contains("green"), "replace next applies");
            HistoryRequested(RightCode, false); await _editTask;
            Require(_rightBuffer.Text == cssRight, "Replace-next undo restores in one step");
            ReplaceAllClick(this, new RoutedEventArgs()); await _editTask;
            Require(!_rightBuffer.Text.Contains("blue") && _rightBuffer.Text.Contains("green"), "replace all applies");
            HistoryRequested(RightCode, false); await _editTask;
            Require(_rightBuffer.Text == cssRight, "Replace-all undo restores in one step");
            FindBox.Text = ""; ReplaceBox.Text = "";

            // Column selection (Alt+drag rectangle): copy spans lines; typing edits every line.
            RightCode.SelectColumn(0, 1, 1, 4);
            string rectangle = RightCode.SelectedText();
            Require(rectangle == " { \n { ", "column copy: [" + rectangle.Replace("\n", "\\n") + "]");
            RightCode.ReplaceSelection("//", "Column");
            string[] rightLines = _rightBuffer.Text.Split('\n');
            Require(rightLines[0].StartsWith("a//") && rightLines[1].StartsWith("b//"), "column typing edits every covered line");
            int guard = 0;
            while (_rightBuffer.Text != cssRight && _rightBuffer.CanUndo && guard++ < 12) { HistoryRequested(RightCode, false); await _editTask; }
            Require(_rightBuffer.Text == cssRight, "column edits undo back to the original");

            // Copy-all: two independent hunks copied in one pass land on the other side.
            await File.WriteAllTextAsync(LeftPath.Text, "same\nleft1\nsame2\nleft2\n");
            await File.WriteAllTextAsync(RightPath.Text, "same\nright1\nsame2\nright2\n");
            await CompareAsync();
            Require(_presentation is not null && _presentation.Hunks.Count == 2, "two hunks expected");
            await CopyAllBlocks(true);
            Require(_rightBuffer!.Text == _leftBuffer!.Text, "copy-all right must equalize: "
                + _leftBuffer.Text.Replace("\n", "|") + " vs " + _rightBuffer.Text.Replace("\n", "|"));
            Require(!_rightBuffer.Text.Contains("right"), "copy-all must overwrite right hunks");

            // Wrapping: a long row spans several visual lines, both sides share the height.
            string longText = new string('x', 500) + "\nsecond\n";
            LeftPath.Text = Path.Combine(root, "wrap-left.txt"); RightPath.Text = Path.Combine(root, "wrap-right.txt");
            await File.WriteAllTextAsync(LeftPath.Text, longText); await File.WriteAllTextAsync(RightPath.Text, new string('y', 500) + "\nsecond\n");
            await CompareAsync();
            double single = LeftCode.RowHeightOf(0);
            WrapToggle.IsChecked = true;
            double wrapped = LeftCode.RowHeightOf(0);
            Require(wrapped > single * 2, $"wrapped row must be taller: {wrapped} vs {single}");
            Require(Math.Abs(RightCode.RowHeightOf(0) - wrapped) < .5, "both sides share the wrapped row height");
            LeftCode.SetCaret(_leftBuffer!.Text.Length - 2);   // hit-testing path must stay sane while wrapped
            Require(LeftCode.CaretOffset > 0, "caret placement while wrapped");
            WrapToggle.IsChecked = false;
            Require(Math.Abs(LeftCode.RowHeightOf(0) - single) < .5, "unwrapping restores single-line height");

            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (Arg("--screenshot") is string screenshot) SaveScreenshot(screenshot);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "edit-smoke-result.json"), System.Text.Json.JsonSerializer.Serialize(new { success = true, root, tests = "both-side edit, undo/redo, bidirectional block merge, save, saved checkpoint, empty-buffer edit, css format, css split, manual/auto realign, find/replace, column selection" }));
        }
        catch (Exception ex) { exit = 2; File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "edit-smoke-error.txt"), ex.ToString()); }
        _allowClose = true; Application.Current.Shutdown(exit);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
