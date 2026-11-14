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
        if (lines.Count == 0) { StatusLabel.Text = "请先选中要对齐的行。"; return; }
        _awaitingAlignTarget = true;
        _alignSourceIsLeft = ReferenceEquals(pane, LeftCode);
        _alignSourceLine = lines[0];
        StatusLabel.Text = $"请在{(_alignSourceIsLeft ? "右" : "左")}侧点击目标行完成对齐（Esc 取消）。";
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
        if (targetLine < 1) { StatusLabel.Text = "未选中目标行。"; return; }
        int left = _alignSourceIsLeft ? _alignSourceLine : targetLine;
        int right = _alignSourceIsLeft ? targetLine : _alignSourceLine;
        _alignmentHints.Add((left, right));
        StatusLabel.Text = $"已添加对齐：左行 {left} ↔ 右行 {right}。正在重新对齐…";
        _ = RealignWithHints();
        e.Handled = true;
    }

    private void IsolateSelection(DiffPane pane)
    {
        var lines = pane.SelectedSourceLines();
        if (lines.Count == 0) { StatusLabel.Text = "请先选中要隔离的行。"; return; }
        bool isLeft = ReferenceEquals(pane, LeftCode);
        foreach (var line in lines)
            if (isLeft) _isolatedLeft.Add(line); else _isolatedRight.Add(line);
        StatusLabel.Text = $"已隔离 {lines.Count} 行（不再参与匹配）。正在重新对齐…";
        _ = RealignWithHints();
    }

    private void ClearAlignmentHints()
    {
        _alignmentHints.Clear();
        _isolatedLeft.Clear();
        _isolatedRight.Clear();
        StatusLabel.Text = "已清除所有手动对齐和隔离。";
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
            HunkLabel.Text = $"{presentation.Hunks.Count} 个差异块";
        }
        catch (Exception ex) { StatusLabel.Text = "手动对齐失败：" + ex.Message; }
    }

    /// <summary>Cancel a pending Align With (Esc key).</summary>
    private void CancelAlignWith()
    {
        if (!_awaitingAlignTarget) return;
        _awaitingAlignTarget = false;
        LeftCode.MouseLeftButtonUp -= AlignTargetClicked;
        RightCode.MouseLeftButtonUp -= AlignTargetClicked;
        StatusLabel.Text = "已取消手动对齐。";
    }
}
