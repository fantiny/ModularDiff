using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using ModularDiff.Core;
using ModularDiff.Contracts;

namespace ModularDiff.Gui;

/// <summary>Three-way merge workbench: base + two sides in, one editable result out.
/// Conflicts carry git-style markers (行背景着色), navigate between them and take either
/// side with one click; the result text stays fully editable.</summary>
public partial class MergeWindow : Window
{
    private static Brush Marker => Controls.Theme.ChangedText;
    private static Brush MarkerBack => Controls.Theme.ChangedBackground;
    private static Brush LeftBack => Controls.Theme.DiffModified;
    private static Brush RightBack => Controls.Theme.DiffInserted;
    private readonly string? _baseArg, _leftArg, _rightArg;
    private readonly string? _gitOutputPath;
    private bool _painting;
    private bool _everMerged;
    private int _currentConflict = -1;
    private List<(int Start, int Middle, int End)> _conflicts = [];

    /// <summary>Git mergetool support: pass the MERGED path to the constructor — the window
    /// auto-saves to it on close and fires GitMergeCompleted with the resolution state.
    /// Closing before any merge ran is fail-closed: MERGED is left untouched (git keeps its
    /// own conflict version) and the exit state stays unresolved — an empty result never
    /// silently counts as "resolved". (Never expose this as an init-only property checked
    /// inside the ctor: object initializers run after the ctor, that wiring was dead code.)</summary>
    public string? GitOutputPath => _gitOutputPath;
    public event Action<bool>? GitMergeCompleted;

    private readonly PluginCatalog _plugins;

    public MergeWindow(string? basePath = null, string? leftPath = null, string? rightPath = null, string? gitOutputPath = null)
    {
        _baseArg = basePath; _leftArg = leftPath; _rightArg = rightPath;
        _gitOutputPath = gitOutputPath;
        _plugins = App.Plugins;   // shared catalog, disposed by App
        InitializeComponent();
        LocalizeUI();
        Controls.Lang.Changed += LocalizeUI;
        Closed += (_, _) => Controls.Lang.Changed -= LocalizeUI;
        if (_gitOutputPath is not null)
            Closing += (_, _) =>
            {
                if (!_everMerged) { GitMergeCompleted?.Invoke(false); return; }
                try
                {
                    var text = new TextRange(Result.Document.ContentStart, Result.Document.ContentEnd).Text;
                    string resolved = text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
                    AtomicFile.WriteAllText(_gitOutputPath, resolved);
                    GitMergeCompleted?.Invoke(_conflicts.Count == 0);
                }
                catch { GitMergeCompleted?.Invoke(false); }
            };
        Loaded += async (_, _) =>
        {
            if (_baseArg is not null) BasePath.Text = _baseArg;
            if (_leftArg is not null) LeftPath.Text = _leftArg;
            if (_rightArg is not null) RightPath.Text = _rightArg;
            if (_baseArg is not null && _leftArg is not null && _rightArg is not null) await MergeAsync();
        };
    }

    private static Brush Brush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;

    private void BrowseBase(object sender, RoutedEventArgs e) => Browse(BasePath, Controls.Lang.Get("merge_browse_base"));
    private void BrowseLeft(object sender, RoutedEventArgs e) => Browse(LeftPath, Controls.Lang.Get("merge_browse_left"));
    private void BrowseRight(object sender, RoutedEventArgs e) => Browse(RightPath, Controls.Lang.Get("merge_browse_right"));
    private static void Browse(TextBox box, string title)
    {
        var dialog = new OpenFileDialog { Title = title };
        if (dialog.ShowDialog() is true) box.Text = dialog.FileName;
    }

    /// <summary>Every caption is declared in XAML with a Loc key, so one pass re-applies the whole
    /// window on construction and on each language switch.</summary>
    private void LocalizeUI()
    {
        Controls.Loc.Apply(this);
        if (!_everMerged) StatusLabel.Text = Controls.Lang.Get("merge_hint");
        UpdateNavigation();
    }

    private async void MergeClick(object sender, RoutedEventArgs e) => await MergeAsync();

    /// <summary>测试/程序化入口:与 ▶ 按钮同一合并入口。</summary>
    public Task RunMergeAsync() => MergeAsync();

    private async Task MergeAsync()
    {
        try
        {
            if (!File.Exists(BasePath.Text) || !File.Exists(LeftPath.Text) || !File.Exists(RightPath.Text))
            { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("merge_need_three_files"); return; }
            StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("merge_working");
            string baseText = await File.ReadAllTextAsync(BasePath.Text);
            string leftText = await File.ReadAllTextAsync(LeftPath.Text);
            string rightText = await File.ReadAllTextAsync(RightPath.Text);
            string[] Split(string text) => text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
            var merged = await Task.Run(() => ThreeWayMerger.Merge(Split(baseText), Split(leftText), Split(rightText), "myers", _plugins));
            string newLine = leftText.Contains("\r\n") ? "\r\n" : "\n";
            string composed = merged.Compose(Path.GetFileName(LeftPath.Text), Path.GetFileName(RightPath.Text), newLine);
            _painting = true;
            Result.Document.Blocks.Clear();
            foreach (string line in composed.Replace("\r\n", "\n").Split('\n'))
                Result.Document.Blocks.Add(new Paragraph(new Run(line)) { Margin = new(0, 0, 0, 0) });
            _painting = false;
            PaintConflicts();
            _everMerged = true;
            StatusLabel.Text = merged.Clean ? ModularDiff.Gui.Controls.Lang.Get("merge_clean_status") : ModularDiff.Gui.Controls.Lang.Get("merge_conflicts_status", merged.Conflicts);
        }
        catch (Exception ex) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("merge_failed", Controls.Lang.Text(ex.Message)); }
    }

    /// <summary>Re-parses conflict markers, colors the blocks and refreshes navigation state.</summary>
    private void PaintConflicts()
    {
        _conflicts = [];
        var paragraphs = Result.Document.Blocks.OfType<Paragraph>().ToList();
        int state = 0, start = -1, middle = -1;   // 0 none, 1 in left part, 2 in right part
        for (int i = 0; i < paragraphs.Count; i++)
        {
            string text = new TextRange(paragraphs[i].ContentStart, paragraphs[i].ContentEnd).Text;
            var range = new TextRange(paragraphs[i].ContentStart, paragraphs[i].ContentEnd);
            range.ClearAllProperties();
            if (text.StartsWith("<<<<<<<", StringComparison.Ordinal))
            {
                state = 1; start = i;
                Style(range, Marker, MarkerBack);
            }
            else if (text.StartsWith("=======", StringComparison.Ordinal) && state == 1)
            {
                state = 2; middle = i;
                Style(range, Marker, MarkerBack);
            }
            else if (text.StartsWith(">>>>>>>", StringComparison.Ordinal) && state == 2)
            {
                _conflicts.Add((start, middle, i));
                state = 0;
                Style(range, Marker, MarkerBack);
            }
            else if (state == 1) Style(range, Ink(), LeftBack);
            else if (state == 2) Style(range, Ink(), RightBack);
        }
        if (_currentConflict >= _conflicts.Count) _currentConflict = _conflicts.Count - 1;
        if (_currentConflict < 0 && _conflicts.Count > 0) _currentConflict = 0;
        UpdateNavigation();
    }

    private static new void Style(TextRange range, Brush foreground, Brush background)
    {
        range.ApplyPropertyValue(TextElement.ForegroundProperty, foreground);
        range.ApplyPropertyValue(TextElement.BackgroundProperty, background);
    }
    private static Brush Ink() => System.Windows.Media.Brushes.Black;

    private void UpdateNavigation()
    {
        bool any = _conflicts.Count > 0;
        PreviousConflict.IsEnabled = NextConflict.IsEnabled = any;
        TakeLeft.IsEnabled = TakeRight.IsEnabled = any;
        Save.IsEnabled = Result.Document.Blocks.Count > 0;
        ConflictLabel.Text = any
            ? Controls.Lang.Get("merge_conflict_index", _currentConflict + 1, _conflicts.Count)
            : _painting ? "" : Controls.Lang.Get(_everMerged ? "merge_no_conflicts" : "merge_not_merged");
    }

    private void ResultChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_painting) return;
        _painting = true;
        PaintConflicts();
        _painting = false;
    }

    private void PreviousConflictClick(object sender, RoutedEventArgs e) => Navigate(-1);
    private void NextConflictClick(object sender, RoutedEventArgs e) => Navigate(1);
    private void Navigate(int delta)
    {
        if (_conflicts.Count == 0) return;
        _currentConflict = (_currentConflict + delta + _conflicts.Count) % _conflicts.Count;
        var paragraphs = Result.Document.Blocks.OfType<Paragraph>().ToList();
        var range = _conflicts[_currentConflict];
        paragraphs[range.Start].BringIntoView();
        paragraphs[range.Start].Background = MarkerBack;
        UpdateNavigation();
    }

    private void TakeLeftClick(object sender, RoutedEventArgs e) => TakeSide(left: true);
    private void TakeRightClick(object sender, RoutedEventArgs e) => TakeSide(left: false);

    /// <summary>Resolves the current conflict by replacing the marked block with one side's lines.
    /// Public as the test/programmatic entry (same path as the ⇐/⇒ buttons).</summary>
    public void TakeSide(bool left)
    {
        if (_currentConflict < 0 || _currentConflict >= _conflicts.Count) return;
        var range = _conflicts[_currentConflict];
        var paragraphs = Result.Document.Blocks.OfType<Paragraph>().ToList();
        int from = left ? range.Start + 1 : range.Middle + 1;
        int to = left ? range.Middle : range.End;
        for (int i = range.End; i >= range.Start; i--)
        {
            if (i >= from && i < to) continue;   // keep the chosen side's lines
            Result.Document.Blocks.Remove(paragraphs[i]);
        }
        _currentConflict = Math.Min(_currentConflict, _conflicts.Count - 2);
        PaintConflicts();
        StatusLabel.Text = _conflicts.Count == 0 ? ModularDiff.Gui.Controls.Lang.Get("merge_all_resolved") : ModularDiff.Gui.Controls.Lang.Get("merge_took_side", ModularDiff.Gui.Controls.Lang.Get(left ? "side_left" : "side_right"), _conflicts.Count);
    }

    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = Controls.Lang.Get("merge_save_dialog_title"),
            FileName = string.IsNullOrWhiteSpace(LeftPath.Text) ? "merged.txt" : Path.GetFileName(LeftPath.Text),
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var text = new TextRange(Result.Document.ContentStart, Result.Document.ContentEnd).Text;
            await File.WriteAllTextAsync(dialog.FileName, text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine));
            StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("merge_saved", dialog.FileName);
        }
        catch (Exception ex) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("merge_save_failed", Controls.Lang.Text(ex.Message)); }
    }
}
