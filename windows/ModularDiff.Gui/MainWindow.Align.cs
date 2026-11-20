using System.Windows;
using System.Windows.Controls;
using ModularDiff.Contracts;
using ModularDiff.Gui.Controls;

namespace ModularDiff.Gui;

/// <summary>Manual alignment (Beyond Compare's Align With / Isolate):
/// select lines on one side, right-click "Align with…", then click a line on the other side
/// to force them side-by-side. "Isolate" marks lines as standalone (never paired).</summary>
public partial class MainWindow
{
    private readonly List<(int LeftLine, int RightLine)> _alignmentHints = [];
    private readonly HashSet<int> _isolatedLeft = [], _isolatedRight = [];
    private bool _awaitingAlignTarget;
    private int _alignSourceLine = -1;
    private bool _alignSourceIsLeft;

    /// <summary>Right-click → "Align with…": captures the source lines, then the next click
    /// on the other side picks the target line and re-runs the presentation with the hint.</summary>
    private void StartAlignWith(DiffPane pane)
    {
        var lines = pane.SelectedSourceLines();
        if (lines.Count == 0) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_select_align_rows"); return; }
        _awaitingAlignTarget = true;
        _alignSourceIsLeft = ReferenceEquals(pane, LeftCode);
        _alignSourceLine = lines[0];
        StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_align_pick_target", ModularDiff.Gui.Controls.Lang.Get(_alignSourceIsLeft ? "side_right" : "side_left"));
        // Wire a one-shot click handler on the other pane.
        var target = _alignSourceIsLeft ? RightCode : LeftCode;
        target.MouseLeftButtonUp -= AlignTargetClicked;
        target.MouseLeftButtonUp += AlignTargetClicked;
    }

    private void AlignTargetClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_awaitingAlignTarget) return;
        var target = (DiffPane)sender;
        target.MouseLeftButtonUp -= AlignTargetClicked;
        _awaitingAlignTarget = false;
        int targetLine = target.SelectedSourceLines().FirstOrDefault();
        if (targetLine < 1) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_align_no_target"); return; }
        int left = _alignSourceIsLeft ? _alignSourceLine : targetLine;
        int right = _alignSourceIsLeft ? targetLine : _alignSourceLine;
        _alignmentHints.Add((left, right));
        StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_align_added", left, right);
        _ = RealignWithHints();
        e.Handled = true;
    }

    private void IsolateSelection(DiffPane pane)
    {
        var lines = pane.SelectedSourceLines();
        if (lines.Count == 0) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_select_isolate"); return; }
        bool isLeft = ReferenceEquals(pane, LeftCode);
        foreach (var line in lines)
            if (isLeft) _isolatedLeft.Add(line); else _isolatedRight.Add(line);
        StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_isolated", lines.Count);
        _ = RealignWithHints();
    }

    private void ClearAlignmentHints()
    {
        _alignmentHints.Clear();
        _isolatedLeft.Clear();
        _isolatedRight.Clear();
        StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_align_cleared");
        _ = RealignWithHints();
    }

    private async Task RealignWithHints()
    {
        if (_textFile is null || PresentationBox.SelectedItem is not ITextPresentation presenter) return;
        try
        {
            var hints = new List<AlignmentHint>();
            foreach (var (l, r) in _alignmentHints) hints.Add(new(l, r));
            var isolated = new List<int>();
            isolated.AddRange(_isolatedLeft);
            isolated.AddRange(_isolatedRight);
            var presentation = await System.Threading.Tasks.Task.Run(() =>
                presenter.Build(_textFile.Lines, TextFilter.SelectedIndex switch { 1 => 0, 2 => 3, _ => -1 },
                    cancellationToken: default, hints: hints, isolatedLines: isolated));
            _presentation = presentation;
            _presentationVersion++;
            _syncText = true;
            LeftCode.SetRows(presentation.Rows); RightCode.SetRows(presentation.Rows);
            _syncText = false;
            Overview.SetPresentation(presentation);
            HunkLabel.Text = ModularDiff.Gui.Controls.Lang.Get("hunks_count", presentation.Hunks.Count);
        }
        catch (Exception ex) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_align_failed", ex.Message); }
    }

    /// <summary>Cancel a pending Align With (Esc key).</summary>
    private void CancelAlignWith()
    {
        if (!_awaitingAlignTarget) return;
        _awaitingAlignTarget = false;
        LeftCode.MouseLeftButtonUp -= AlignTargetClicked;
        RightCode.MouseLeftButtonUp -= AlignTargetClicked;
        StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_align_cancelled");
    }
}
