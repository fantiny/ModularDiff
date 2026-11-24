using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ModularDiff.Contracts;

namespace ModularDiff.Gui;

/// <summary>Folder-view context menu: open a file pair, copy one file to the other side
/// (with overwrite confirmation), reveal in Windows Explorer — like Beyond Compare.</summary>
public partial class MainWindow
{
    private void InitializeFolderOps()
    {
        LeftFolders.ContextMenuOpening += FolderMenuOpening;
        RightFolders.ContextMenuOpening += FolderMenuOpening;
    }

    private void FolderMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_folderResult is null) return;
        var grid = (Controls.FolderList)sender;
        if (grid.SelectedItem is not FolderNode node) return;
        e.Handled = true;
        bool fromLeft = ReferenceEquals(sender, LeftFolders);
        var selected = grid.SelectedItems.Cast<FolderNode>().Where(n => !n.IsDirectory).ToArray();
        if (selected.Length > 1)
        {
            var multi = new ContextMenu();
            var copyAll = new MenuItem { Header = ModularDiff.Gui.Controls.Lang.Get("folder_copy_n", selected.Length, ModularDiff.Gui.Controls.Lang.Get(fromLeft ? "side_right" : "side_left")) };
            copyAll.Click += (_, _) => _ = CopyFolderFiles(selected, fromLeft);
            multi.Items.Add(copyAll);
            multi.PlacementTarget = grid;
            multi.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            multi.IsOpen = true;
            return;
        }
        string leftPath = Path.Combine(_folderResult.LeftPath, node.RelativePath);
        string rightPath = Path.Combine(_folderResult.RightPath, node.RelativePath);
        string clicked = fromLeft ? leftPath : rightPath;
        var menu = new ContextMenu();
        void Item(string header, bool enabled, Action action)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        if (!node.IsDirectory)
        {
            var other = fromLeft ? RightFolders.SelectedItem as FolderNode : LeftFolders.SelectedItem as FolderNode;
            bool cross = other is { IsDirectory: false }
                && !string.Equals(node.RelativePath, other.RelativePath, StringComparison.OrdinalIgnoreCase)
                && (fromLeft ? node.LeftExists && other.RightExists : other.LeftExists && node.RightExists);
            string crossTitle = fromLeft ? $"{node.Name} ↔ {other!.Name}" : $"{other!.Name} ↔ {node.Name}";
            Item(cross ? ModularDiff.Gui.Controls.Lang.Get("menu_open_compare_cross", crossTitle) : ModularDiff.Gui.Controls.Lang.Get("menu_open_compare"), true, () => _ = OpenSelectedFiles());
            if (cross)
                Item(ModularDiff.Gui.Controls.Lang.Get("menu_open_paired_only"), true, () => _ = OpenNode(node));
            Item(fromLeft ? ModularDiff.Gui.Controls.Lang.Get("menu_copy_to_right") : ModularDiff.Gui.Controls.Lang.Get("menu_copy_to_left"), File.Exists(clicked), () => _ = CopyFolderFile(node, fromLeft));
        }
        Item(fromLeft ? ModularDiff.Gui.Controls.Lang.Get("menu_reveal_left") : ModularDiff.Gui.Controls.Lang.Get("menu_reveal_right"), File.Exists(clicked) || Directory.Exists(clicked),
            () => Process.Start("explorer.exe", "/select,\"" + clicked + "\""));
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>Batch-copies multiple files to the other side; skips missing sources, keeps going on errors.</summary>
    private async Task CopyFolderFiles(FolderNode[] nodes, bool fromLeft)
    {
        if (_folderResult is null) return;
        int copied = 0, failed = 0;
        foreach (var node in nodes)
        {
            try
            {
                string source = Path.Combine(_folderResult.LeftPath, node.RelativePath);
                string target = Path.Combine(_folderResult.RightPath, node.RelativePath);
                if (!fromLeft) (source, target) = (target, source);
                if (!File.Exists(source)) { failed++; continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
                copied++;
            }
            catch (Exception) { failed++; }
        }
        string reopen = _currentNode?.RelativePath ?? nodes.FirstOrDefault()?.RelativePath ?? "";
        await CompareAsync();
        var restored = _folderRows.FirstOrDefault(n => n.RelativePath == reopen);
        if (restored is not null) LeftFolders.SelectedItem = RightFolders.SelectedItem = restored;
        StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_batch_copy_done", copied, failed > 0 ? ModularDiff.Gui.Controls.Lang.Get("status_batch_copy_fail_suffix", failed) : "");
    }

    private async Task CopyFolderFile(FolderNode node, bool fromLeft)
    {
        if (_folderResult is null) return;
        string source = Path.Combine(_folderResult.LeftPath, node.RelativePath);
        string target = Path.Combine(_folderResult.RightPath, node.RelativePath);
        if (!fromLeft) (source, target) = (target, source);
        try
        {
            if (!File.Exists(source)) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_source_missing", source); return; }
            if (File.Exists(target))
            {
                var choice = MessageBox.Show(this, ModularDiff.Gui.Controls.Lang.Get("dlg_copy_overwrite", target), ModularDiff.Gui.Controls.Lang.Get("dlg_copy_overwrite_title"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (choice != MessageBoxResult.Yes) return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            string reopen = _currentNode?.RelativePath ?? node.RelativePath;
            await CompareAsync();   // refresh the folder comparison after the copy
            var restored = _folderRows.FirstOrDefault(n => n.RelativePath == reopen);
            if (restored is not null) LeftFolders.SelectedItem = RightFolders.SelectedItem = restored;
            StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_copied_to", target);
        }
        catch (Exception ex) { StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_copy_failed", ModularDiff.Gui.Controls.Lang.Text(ex.Message)); }
    }
}
