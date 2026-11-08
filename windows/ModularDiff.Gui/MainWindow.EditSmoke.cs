using System.IO;
using System.Windows;
using System.Windows.Threading;

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
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (Arg("--screenshot") is string screenshot) SaveScreenshot(screenshot);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "edit-smoke-result.json"), System.Text.Json.JsonSerializer.Serialize(new { success = true, root, tests = "both-side edit, undo/redo, bidirectional block merge, save, saved checkpoint, empty-buffer edit" }));
        }
        catch (Exception ex) { exit = 2; File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "edit-smoke-error.txt"), ex.ToString()); }
        _allowClose = true; Application.Current.Shutdown(exit);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
