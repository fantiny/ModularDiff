using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ModularDiff.Contracts;
using ModularDiff.Core;
using ModularDiff.Gui.Controls;

namespace ModularDiff.Gui;

/// <summary>Filter-as-you-type regression smoke (<c>--filter-smoke-test</c>, run by
/// cli-smoke.ps1 -Gui): the folder lists must keep their row visuals while the filter changes,
/// scroll and navigate by re-pointing them, and answer a keystroke within a fixed budget
/// whatever the size of the tree.</summary>
public partial class MainWindow
{
    private async void FilterSmokeTest(object sender, RoutedEventArgs e)
    {
        int exit = 0;
        var report = new Dictionary<string, object?> { ["success"] = false };
        try
        {
            FolderSearchRegex.IsChecked = false;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            FolderSearch.Focus();
            foreach (int files in new[] { 20_000, 100_000 })
            {
                _folderResult = SyntheticFolders(files);
                _rootRequest = new ComparisonRequest(_folderResult.LeftPath, _folderResult.RightPath, "myers", new());
                FolderSearch.Text = "";
                _tree = await Task.Run(() => FolderTree.Build(_folderResult, default));
                FolderSearch.Text = "j";
                UpdateLayout();

                // "j" keeps the .json and the .js files, the folder of every library and the root.
                int expected = files / 5 + files / 25 + 1;
                Require(LeftFolders.Rows.Count == expected && RightFolders.Rows.Count == expected,
                    $"The filter must list {expected} rows of {files} files (left shows {LeftFolders.Rows.Count}).");
                if (files == 20_000) await FilterSmokeLists();
                (double steady, double turnover, double typed) = MeasureFilterKeystrokes();
                report[$"{files}:steadyMs"] = Math.Round(steady, 1);
                report[$"{files}:turnoverMs"] = Math.Round(turnover, 1);
                report[$"{files}:typedMs"] = Math.Round(typed, 1);
                // The budget is about three times what the development machine measures: a regression
                // that rebuilds rows again lands far above it (it was 150-260 ms before FolderList).
                Require(steady < 80, $"A keystroke that keeps the row set took {steady:0} ms (budget 80).");
                Require(typed < 100, $"A typed character took {typed:0} ms (budget 100).");
                Require(turnover < 160, $"A keystroke that replaces every visible row took {turnover:0} ms (budget 160).");
            }
            report["success"] = true;
        }
        catch (Exception ex) { exit = 2; report["error"] = ex.Message; File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "filter-smoke-error.txt"), ex.ToString()); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "filter-smoke-result.json"), JsonSerializer.Serialize(report));
        _allowClose = true; Application.Current.Shutdown(exit);
    }

    /// <summary>What the pooled lists owe the user beyond speed: fixed row visuals, re-pointed by
    /// filtering and scrolling; keyboard navigation; per-side selection; a header above the columns;
    /// a row redrawn when its own node changes under it.</summary>
    private async Task FilterSmokeLists()
    {
        var host = FindChild<FolderRowHost>(LeftFolders) ?? throw new InvalidOperationException("The folder list has no row host.");
        var scroll = FindChild<ScrollViewer>(LeftFolders) ?? throw new InvalidOperationException("The folder list has no scroll viewer.");
        var rows = LeftFolders.Rows;
        Require(host.RowCount > 5 && host.RowCount < 80, $"The list must hold one row visual per visible row, not per item ({host.RowCount}).");
        Require(VisualTreeHelper.GetChildrenCount(host) == host.RowCount, "Every pooled row must be in the visual tree.");

        // Filtering re-points the same visuals: no row is rebuilt, and the text on screen is the new node's.
        var visual = (FrameworkElement)VisualTreeHelper.GetChild(host, 4);
        var nameCell = FindChild<DockPanel>(visual) ?? throw new InvalidOperationException("The row visual has no name cell.");
        FolderNode before = (FolderNode)visual.DataContext;
        FolderSearch.Text = "jso"; UpdateLayout();
        FolderNode after = rows[4];
        Require(ReferenceEquals(visual, VisualTreeHelper.GetChild(host, 4)), "A filter keystroke must keep the row visuals.");
        Require(ReferenceEquals(visual.DataContext, after) && !ReferenceEquals(before, after), "The fifth row visual must now show the fifth node of the new row set.");
        Require(nameCell.Children.OfType<TextBlock>().Last().Text == after.LeftName, $"The row must show '{after.LeftName}' (shows '{nameCell.Children.OfType<TextBlock>().Last().Text}').");
        Require(!ReferenceEquals(VisualTreeHelper.GetChild(host, 3), VisualTreeHelper.GetChild(FindChild<FolderRowHost>(RightFolders)!, 4)), "Each list has its own row visuals.");

        // The header sits on the columns: same left edges (its cells have an 8 px inset, the rows' 5 px) and the rows begin under it.
        var header = LeftFolders.Header as Border ?? throw new InvalidOperationException("The folder list has no header.");
        var headerCells = ((Grid)header.Child!).Children.OfType<TextBlock>().ToList();
        var rowCells = ((Grid)((Border)VisualTreeHelper.GetChild(host, 0)).Child!).Children.OfType<TextBlock>().Where(cell => Grid.GetColumn(cell) > 0).ToList();
        Require(headerCells.Count == 4 && rowCells.Count == 3, "The header must name the four columns and a row must have the three text columns.");
        for (int i = 0; i < 3; i++)
        {
            double edge = headerCells[i + 1].TranslatePoint(new Point(), LeftFolders).X - rowCells[i].TranslatePoint(new Point(), LeftFolders).X;
            Require(Math.Abs(edge - 3) < 0.6, $"Column '{headerCells[i + 1].Text}' must sit above its cells (offset {edge:0.0}).");
        }
        double seam = header.TranslatePoint(new Point(0, header.ActualHeight), LeftFolders).Y - host.TranslatePoint(new Point(), LeftFolders).Y;
        Require(Math.Abs(seam) < 0.6, $"The first row must start right below the header (gap {seam:0.0}).");

        // Scrolling re-points rows too, and the other side follows.
        scroll.ScrollToBottom(); UpdateLayout();
        Require(host.FirstRow == rows.Count - host.RowCount, $"Scrolled to the end the first row must be {rows.Count - host.RowCount} ({host.FirstRow}).");
        Require(ReferenceEquals(((FrameworkElement)VisualTreeHelper.GetChild(host, host.RowCount - 1)).DataContext, rows[^1]), "The last pooled row must show the last node.");
        Require(FindChild<FolderRowHost>(RightFolders)!.FirstRow == host.FirstRow, "The two sides must scroll together.");
        scroll.ScrollToTop(); UpdateLayout();
        Require(host.FirstRow == 0, "Back at the top.");
        int notch = SystemParameters.WheelScrollLines;
        scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.MouseWheelEvent });
        UpdateLayout();
        Require(host.FirstRow == notch, $"One wheel notch must scroll {notch} rows ({host.FirstRow}).");
        Require(ReferenceEquals(((FrameworkElement)VisualTreeHelper.GetChild(host, 0)).DataContext, rows[notch]), "The first row visual must show the first scrolled-in node.");
        scroll.ScrollToTop(); UpdateLayout();

        // Keyboard: the arrows move the selection, End and Page keys move it by the screen, the list scrolls along.
        Require(LeftFolders.Focus(), "The folder list must take the keyboard focus.");
        LeftFolders.SelectedItem = rows[0];
        for (int i = 0; i < 3; i++) PressKey(LeftFolders, Key.Down);
        Require(ReferenceEquals(LeftFolders.SelectedItem, rows[3]), "Down must move the selection one row.");
        PressKey(LeftFolders, Key.End);
        Require(ReferenceEquals(LeftFolders.SelectedItem, rows[^1]) && host.FirstRow == rows.Count - host.RowCount, "End must select the last row and scroll it into view.");
        PressKey(LeftFolders, Key.Home);
        Require(ReferenceEquals(LeftFolders.SelectedItem, rows[0]) && host.FirstRow == 0, "Home must select the first row.");
        PressKey(LeftFolders, Key.PageDown);
        Require(ReferenceEquals(LeftFolders.SelectedItem, rows[host.RowCount - 1]), "PageDown must move the selection by one screen.");
        UpdateLayout();   // the rows pick up the selection flags on their next pass
        Require(FolderRowHost.GetIsCurrent(VisualTreeHelper.GetChild(host, host.RowCount - 1)) && !FolderRowHost.GetIsCurrent(VisualTreeHelper.GetChild(host, 0)),
            "The row the keyboard acts on must be marked as the current row, and only that one.");

        // Selection is per side, multi-selection marks its rows, and a filtered-out selection falls back to the first row.
        LeftFolders.SelectedItems.Clear(); RightFolders.SelectedItems.Clear();
        LeftFolders.SelectedItems.Add(LeftFolders.Rows[10]); LeftFolders.SelectedItems.Add(LeftFolders.Rows[11]);
        Require(LeftFolders.SelectedItems.Count == 2 && RightFolders.SelectedItems.Count == 0, "Selecting on one side must not select on the other.");
        UpdateLayout();
        Require(FolderRowHost.GetIsSelected(VisualTreeHelper.GetChild(host, 10)) && FolderRowHost.GetIsSelected(VisualTreeHelper.GetChild(host, 11))
            && !FolderRowHost.GetIsSelected(VisualTreeHelper.GetChild(host, 12)), "Selected rows must be drawn selected.");
        FolderSearch.Text = "j"; UpdateLayout();
        Require(LeftFolders.Rows.Count == rows.Count, "Typing the query backwards must bring the rows back.");
        LeftFolders.SelectedItem = LeftFolders.Rows.First(node => !node.IsDirectory && node.Name.EndsWith(".js", StringComparison.OrdinalIgnoreCase));
        Require(LeftFolders.SelectedItems.Count == 1, "Setting the selected item replaces the selection.");
        FolderSearch.Text = "jso"; UpdateLayout();
        Require(ReferenceEquals(LeftFolders.SelectedItem, LeftFolders.Rows[0]), "When the selection is filtered out the first row is selected.");

        FolderSearch.Text = ""; UpdateLayout();
            Require(LeftFolders.Rows.Count == 1 && host.FirstRow == 0 && ((FrameworkElement)VisualTreeHelper.GetChild(host, 0)).Visibility == Visibility.Visible
            && host.RowCount > 1 && ((FrameworkElement)VisualTreeHelper.GetChild(host, 1)).Visibility == Visibility.Collapsed,
            "Without a filter only the collapsed root is listed, on the first of the pooled rows.");

        // A node that changes under its row must be redrawn in place. The pool keeps a row's visuals and its
        // data context while the node stays at that position — which is exactly when nothing re-points the
        // row: a folder that expands adds rows below it, a file that resolves keeps its row.
        string Arrow(FrameworkElement row) =>
            (FindChild<DockPanel>(row) ?? throw new InvalidOperationException("The row visual has no name cell.")).Children.OfType<TextBlock>().First().Text;
        string Glyph(Border row) => ((Grid)row.Child!).Children.OfType<TextBlock>().First().Text;
        var rootRow = (Border)VisualTreeHelper.GetChild(host, 0);
        Require(rows[0].IsDirectory && ReferenceEquals(rootRow.DataContext, rows[0]) && Arrow(rootRow) == "▸", $"The collapsed root must show a closed arrow (shows '{Arrow(rootRow)}').");
        // The root, its first library and that library's src folder, opened one after the other, put a file row on screen.
        ToggleFolder(rows[0]); ToggleFolder(rows[1]); ToggleFolder(rows[2]); UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);   // a binding takes its new value in a later pass
        Require(ReferenceEquals(rootRow, VisualTreeHelper.GetChild(host, 0)) && ReferenceEquals(rootRow.DataContext, rows[0]), "Expanding the root must keep its row on the root.");
        Require(Arrow(rootRow) == "▾", $"An expanded folder must show an open arrow (shows '{Arrow(rootRow)}').");
        FolderNode resolved = rows[3];
        var libraryRow = (Border)VisualTreeHelper.GetChild(host, 1);
        var fileRow = (Border)VisualTreeHelper.GetChild(host, 3);
        Require(!resolved.IsDirectory && ReferenceEquals(fileRow.DataContext, resolved) && Glyph(fileRow) == resolved.StatusGlyph, "The fourth row must be the first file of the first library.");
        FileStatus statusBefore = resolved.Status;
        Require(_tree!.ApplyFileUpdate(new FileComparison(resolved.RelativePath, FileStatus.Error, []), _folderResult!.LeftPath, _folderResult.RightPath), "The file is in the tree.");
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Require(Glyph(fileRow) == "!" && Glyph(libraryRow) == "!" && Glyph(rootRow) == "!",
            $"A file that turns into an error under its row, and the folders above it, must be redrawn (shows '{Glyph(fileRow)}', '{Glyph(libraryRow)}', '{Glyph(rootRow)}').");
        _tree.ApplyFileUpdate(new FileComparison(resolved.RelativePath, statusBefore, []), _folderResult.LeftPath, _folderResult.RightPath);
        ToggleFolder(rows[2]); ToggleFolder(rows[1]); ToggleFolder(rows[0]); UpdateLayout();   // back as the smoke found it
    }

    /// <summary>Median milliseconds of a keystroke, with the layout it causes: one that keeps the row
    /// set ("jso" → "json"), one that replaces every visible row ("x" → "j"), and one character
    /// fed through the text-input pipeline the keyboard uses.</summary>
    private (double Steady, double Turnover, double Typed) MeasureFilterKeystrokes()
    {
        double Steady() { FolderSearch.Text = "jso"; UpdateLayout(); return Keystroke("json"); }
        double Turnover() { FolderSearch.Text = "x"; UpdateLayout(); return Keystroke("j"); }
        double typed = Median(() =>
        {
            FolderSearch.Text = ""; UpdateLayout();
            double worst = 0;
            foreach (char letter in "json") worst = Math.Max(worst, Type(letter));
            return worst;
        });
        return (Median(Steady), Median(Turnover), typed);
    }

    private double Keystroke(string text)
    {
        var clock = Stopwatch.StartNew();
        FolderSearch.Text = text;
        UpdateLayout();
        return clock.Elapsed.TotalMilliseconds;
    }

    private double Type(char letter)
    {
        var clock = Stopwatch.StartNew();
        TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, FolderSearch, letter.ToString()));
        UpdateLayout();
        return clock.Elapsed.TotalMilliseconds;
    }

    private static double Median(Func<double> measure)
    {
        for (int i = 0; i < 5; i++) measure();
        var samples = new double[15];
        for (int i = 0; i < samples.Length; i++) samples[i] = measure();
        Array.Sort(samples);
        return samples[samples.Length / 2];
    }

    private static void PressKey(UIElement target, Key key) =>
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });

    /// <summary>A folder comparison result with <paramref name="count"/> files under one library
    /// folder per 50 of them (each with a src folder) and a realistic mix of extensions — no files
    /// on disk are needed. Every fifth file is a .json or .js file, every tenth a .json file.</summary>
    private static ComparisonResult SyntheticFolders(int count)
    {
        string root = Path.Combine(Path.GetTempPath(), "ModularDiff-filter-smoke");
        string[] extensions = [".json", ".cs", ".dll", ".pdb", ".xml", ".ts", ".js", ".md", ".png", ".txt"];
        var files = new List<FileComparison>(count);
        for (int i = 0; i < count; i++)
        {
            string relative = $"packages/Lib{i / 50:d3}/src/File{i:d6}{extensions[i % extensions.Length]}";
            files.Add(new FileComparison(relative, i % 3 == 0 ? FileStatus.Modified : FileStatus.Equal, []));
        }
        return new ComparisonResult(Path.Combine(root, "left"), Path.Combine(root, "right"), "directories", "myers", files, []);
    }
}
