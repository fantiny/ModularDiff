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
        var grid = (DataGrid)sender;
        if (grid.SelectedItem is not FolderNode node) return;
        e.Handled = true;
        bool fromLeft = ReferenceEquals(sender, LeftFolders);
        var selected = grid.SelectedItems.Cast<FolderNode>().Where(n => !n.IsDirectory).ToArray();
        if (selected.Length > 1)
        {
            var multi = new ContextMenu();
            var copyAll = new MenuItem { Header = $"复制 {selected.Length} 个文件到" + (fromLeft ? "右侧" : "左侧") };
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
            Item("打开比较", true, () => _ = OpenNode(node));
            Item("复制到" + (fromLeft ? "右侧" : "左侧"), File.Exists(clicked), () => _ = CopyFolderFile(node, fromLeft));
        }
        Item("在资源管理器中显示(" + (fromLeft ? "左" : "右") + "侧)", File.Exists(clicked) || Directory.Exists(clicked),
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
        StatusLabel.Text = $"批量复制完成：{copied} 成功" + (failed > 0 ? $", {failed} 失败" : "") + "。";
    }

    private async Task CopyFolderFile(FolderNode node, bool fromLeft)
    {
        if (_folderResult is null) return;
        string source = Path.Combine(_folderResult.LeftPath, node.RelativePath);
        string target = Path.Combine(_folderResult.RightPath, node.RelativePath);
        if (!fromLeft) (source, target) = (target, source);
        try
        {
            if (!File.Exists(source)) { StatusLabel.Text = "源文件不存在：" + source; return; }
            if (File.Exists(target))
            {
                var choice = MessageBox.Show(this, "目标已存在，是否覆盖？\n\n" + target, "复制到另一侧", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (choice != MessageBoxResult.Yes) return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            string reopen = _currentNode?.RelativePath ?? node.RelativePath;
            await CompareAsync();   // refresh the folder comparison after the copy
            var restored = _folderRows.FirstOrDefault(n => n.RelativePath == reopen);
            if (restored is not null) LeftFolders.SelectedItem = RightFolders.SelectedItem = restored;
            StatusLabel.Text = "已复制：" + target;
        }
        catch (Exception ex) { StatusLabel.Text = "复制失败：" + ex.Message; }
    }
}
