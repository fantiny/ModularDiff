using System.IO;
using System.Windows;
using ModularDiff.Contracts;

namespace ModularDiff.Gui;

public sealed class FolderNode
{
    public required string Name { get; init; }
    public required string RelativePath { get; init; }
    public bool IsDirectory { get; init; }
    public int Depth { get; init; }
    public bool IsExpanded { get; set; } = true;
    public bool LeftExists { get; set; }
    public bool RightExists { get; set; }
    public FileComparison? File { get; set; }
    public FileStatus Status { get; set; }
    public List<FolderNode> Children { get; } = [];
    public string LeftSize { get; set; } = "";
    public string RightSize { get; set; } = "";
    public string LeftModified { get; set; } = "";
    public string RightModified { get; set; } = "";
    public string LeftName => LeftExists ? Name : "";
    public string RightName => RightExists ? Name : "";
    public string Symbol => IsDirectory ? IsExpanded ? "▾" : "▸" : "·";
    public string StatusGlyph => Status switch { FileStatus.Equal => "=", FileStatus.Modified => "≠", FileStatus.LeftOnly => "‹", FileStatus.RightOnly => "›", _ => "!" };
    public string StatusText => Status switch { FileStatus.Equal => "相同", FileStatus.Modified => "内容不同", FileStatus.LeftOnly => "仅左侧", FileStatus.RightOnly => "仅右侧", _ => "读取错误" };
    public string Color => Status switch { FileStatus.Modified => "#B44337", FileStatus.LeftOnly or FileStatus.RightOnly => "#7956AE", FileStatus.Error => "#B47A14", _ => "#435164" };
    public string KindIcon => IsDirectory ? "▰" : "▤";
    public Thickness Indent => new(Depth * 17, 0, 0, 0);
    public string Description => RelativePath + " · " + StatusText + (File?.Note is null ? "" : " · " + File.Note);
}

public sealed class FolderTree
{
    public List<FolderNode> Roots { get; } = [];
    public IReadOnlyList<FolderNode> All { get; private set; } = [];
    public static FolderTree Build(ComparisonResult result, CancellationToken token)
    {
        var tree = new FolderTree();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var nodes = new Dictionary<string, FolderNode>(comparer);
        foreach (var file in result.Files)
        {
            token.ThrowIfCancellationRequested();
            string[] parts = file.RelativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            string path = ""; FolderNode? parent = null;
            for (int i = 0; i < parts.Length; i++)
            {
                path = path.Length == 0 ? parts[i] : Path.Combine(path, parts[i]);
                if (!nodes.TryGetValue(path, out var node))
                {
                    node = new() { Name = parts[i], RelativePath = path, Depth = i, IsDirectory = i < parts.Length - 1 };
                    nodes.Add(path, node);
                    if (parent is null) tree.Roots.Add(node); else parent.Children.Add(node);
                }
                node.LeftExists |= file.Status != FileStatus.RightOnly;
                node.RightExists |= file.Status != FileStatus.LeftOnly;
                parent = node;
            }
            if (parent is null) continue;
            parent.File = file; parent.Status = file.Status;
            if (parent.LeftExists) (parent.LeftSize, parent.LeftModified) = Metadata(Path.Combine(result.LeftPath, file.RelativePath));
            if (parent.RightExists) (parent.RightSize, parent.RightModified) = Metadata(Path.Combine(result.RightPath, file.RelativePath));
        }
        void Finish(List<FolderNode> list)
        {
            list.Sort((a, b) => a.IsDirectory == b.IsDirectory ? comparer.Compare(a.Name, b.Name) : a.IsDirectory ? -1 : 1);
            foreach (var node in list.Where(n => n.IsDirectory))
            {
                Finish(node.Children);
                node.Status = !node.LeftExists ? FileStatus.RightOnly : !node.RightExists ? FileStatus.LeftOnly
                    : node.Children.Any(c => c.Status == FileStatus.Error) ? FileStatus.Error
                    : node.Children.Any(c => c.Status != FileStatus.Equal) ? FileStatus.Modified : FileStatus.Equal;
            }
        }
        Finish(tree.Roots); tree.All = nodes.Values.ToArray(); return tree;
    }

    public IReadOnlyList<FolderNode> Visible(int filter, string query)
    {
        var rows = new List<FolderNode>();
        var matches = new Dictionary<FolderNode, bool>();
        bool Matches(FolderNode node)
        {
            if (matches.TryGetValue(node, out bool value)) return value;
            bool match = node.IsDirectory ? node.Children.Any(Matches)
                : (filter switch { 1 => node.Status != FileStatus.Equal, 2 => node.Status == FileStatus.Equal,
                    3 => node.Status == FileStatus.LeftOnly, 4 => node.Status == FileStatus.RightOnly, _ => true })
                  && (query.Length == 0 || node.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase));
            matches[node] = match; return match;
        }
        void Add(IEnumerable<FolderNode> list)
        {
            foreach (var node in list)
            {
                if (!Matches(node)) continue;
                rows.Add(node);
                if (node.IsDirectory && (node.IsExpanded || query.Length > 0)) Add(node.Children);
            }
        }
        Add(Roots); return rows;
    }
    private static (string Size, string Modified) Metadata(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return ("—", "不可读取");
            long size = file.Length;
            return (size < 1024 ? $"{size} B" : size < 1024 * 1024 ? $"{size / 1024d:0.0} KB" : $"{size / 1024d / 1024d:0.0} MB", file.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ("—", "不可读取"); }
    }
}
