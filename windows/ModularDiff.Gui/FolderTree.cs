using System.IO;
using System.Windows;
using ModularDiff.Contracts;
using ModularDiff.Core;

namespace ModularDiff.Gui;

public sealed class FolderNode
{
    public required string Name { get; init; }
    public required string RelativePath { get; init; }
    public bool IsDirectory { get; set; }
    public int Depth { get; init; }
    /// <summary>Collapsed by default so huge trees only paint the top level until expanded.</summary>
    public bool IsExpanded { get; set; }
    public bool LeftExists { get; set; }
    public bool RightExists { get; set; }
    public FileComparison? File { get; set; }
    public FileStatus Status { get; set; }
    public List<FolderNode> Children { get; } = [];
    public FolderNode? Parent { get; set; }
    public string LeftSize { get; set; } = "";
    public string RightSize { get; set; } = "";
    public string LeftModified { get; set; } = "";
    public string RightModified { get; set; } = "";
    public string LeftName => LeftExists ? Name : "";
    public string RightName => RightExists ? Name : "";
    public string Symbol => IsDirectory ? IsExpanded ? "▾" : "▸" : "·";
    public string StatusGlyph => Status switch
    {
        FileStatus.Equal => "=",
        FileStatus.Modified => "≠",
        FileStatus.LeftOnly => "‹",
        FileStatus.RightOnly => "›",
        FileStatus.Pending => "…",
        _ => "!",
    };
    public string StatusText => Status switch
    {
        FileStatus.Equal => Controls.Lang.Get("file_status_equal"),
        FileStatus.Modified => Controls.Lang.Get("file_status_modified"),
        FileStatus.LeftOnly => Controls.Lang.Get("file_status_left_only"),
        FileStatus.RightOnly => Controls.Lang.Get("file_status_right_only"),
        FileStatus.Pending => Controls.Lang.Get("file_status_pending"),
        _ => Controls.Lang.Get("file_status_error"),
    };
    public string Color => Status switch
    {
        FileStatus.Modified => "#B44337",
        FileStatus.LeftOnly or FileStatus.RightOnly => "#7956AE",
        FileStatus.Error => "#B47A14",
        FileStatus.Pending => "#8290A3",
        _ => "#435164",
    };
    public string KindIcon => IsDirectory ? "▰" : "▤";
    public Thickness Indent => new(Depth * 17, 0, 0, 0);
    public string Description => RelativePath + " · " + StatusText + (File?.Note is null ? "" : " · " + Controls.Lang.Text(File.Note));
}

public sealed class FolderTree
{
    public List<FolderNode> Roots { get; } = [];
    public IReadOnlyList<FolderNode> All { get; private set; } = [];
    private Dictionary<string, FolderNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private StringComparer _comparer = StringComparer.OrdinalIgnoreCase;
    private string _leftRoot = "", _rightRoot = "";

    public static FolderTree Build(ComparisonResult result, CancellationToken token)
    {
        var tree = new FolderTree
        {
            _comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal,
            _leftRoot = result.LeftPath,
            _rightRoot = result.RightPath,
        };
        tree._nodes = new Dictionary<string, FolderNode>(tree._comparer);
        foreach (var file in result.Files)
        {
            token.ThrowIfCancellationRequested();
            tree.Upsert(file, resort: false);
        }
        Finish(tree.Roots, tree._comparer);
        tree.All = tree._nodes.Values.ToArray();
        return tree;
    }

    /// <summary>Merges newly discovered inventory entries into an existing tree (keeps expand state).</summary>
    public int Merge(ComparisonResult result, CancellationToken token)
    {
        _leftRoot = result.LeftPath;
        _rightRoot = result.RightPath;
        return MergeAdded(result.Files, result.LeftPath, result.RightPath, token);
    }

    /// <summary>Upserts only the provided entries (deltas from BFS waves). Cheap for progressive scan.</summary>
    public int MergeAdded(IEnumerable<FileComparison> added, string leftRoot, string rightRoot, CancellationToken token = default)
    {
        _leftRoot = leftRoot;
        _rightRoot = rightRoot;
        int count = 0;
        foreach (var file in added)
        {
            token.ThrowIfCancellationRequested();
            if (_nodes.ContainsKey(file.RelativePath))
            {
                ApplyFileUpdate(file, leftRoot, rightRoot);
                continue;
            }
            Upsert(file, resort: false);
            count++;
        }
        if (count > 0)
        {
            Finish(Roots, _comparer);
            All = _nodes.Values.ToArray();
        }
        return count;
    }

    /// <summary>Patches one resolved file into an existing tree without rebuilding (keeps expand state).</summary>
    public bool ApplyFileUpdate(FileComparison file, string leftRoot, string rightRoot)
    {
        if (!_nodes.TryGetValue(file.RelativePath, out var node)) return false;
        if (node.IsDirectory != file.IsDirectory && file.IsDirectory) return false;
        node.File = file;
        node.Status = file.Status;
        node.LeftExists |= file.Status is not FileStatus.RightOnly;
        node.RightExists |= file.Status is not FileStatus.LeftOnly;
        if (!node.IsDirectory)
        {
            if (node.LeftExists) (node.LeftSize, node.LeftModified) = Metadata(Path.Combine(leftRoot, file.RelativePath));
            if (node.RightExists) (node.RightSize, node.RightModified) = Metadata(Path.Combine(rightRoot, file.RelativePath));
        }
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            parent.Status = Aggregate(parent);
        return true;
    }

    private void Upsert(FileComparison file, bool resort)
    {
        string[] parts = file.RelativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        string path = ""; FolderNode? parent = null;
        for (int i = 0; i < parts.Length; i++)
        {
            path = path.Length == 0 ? parts[i] : Path.Combine(path, parts[i]);
            bool last = i == parts.Length - 1;
            bool asDirectory = file.IsDirectory || !last;
            if (!_nodes.TryGetValue(path, out var node))
            {
                node = new()
                {
                    Name = parts[i], RelativePath = path, Depth = i,
                    IsDirectory = asDirectory, Parent = parent,
                };
                _nodes.Add(path, node);
                if (parent is null) Roots.Add(node); else parent.Children.Add(node);
            }
            else if (asDirectory) node.IsDirectory = true;
            node.LeftExists |= file.Status is not FileStatus.RightOnly;
            node.RightExists |= file.Status is not FileStatus.LeftOnly;
            parent = node;
        }
        if (parent is null) return;
        parent.File = file;
        parent.Status = file.Status;
        if (!parent.IsDirectory)
        {
            if (parent.LeftExists) (parent.LeftSize, parent.LeftModified) = Metadata(Path.Combine(_leftRoot, file.RelativePath));
            if (parent.RightExists) (parent.RightSize, parent.RightModified) = Metadata(Path.Combine(_rightRoot, file.RelativePath));
        }
        if (resort && parent.Parent is not null) Finish([parent.Parent], _comparer);
        else if (resort) Finish(Roots, _comparer);
    }

    private static void Finish(List<FolderNode> list, StringComparer comparer)
    {
        list.Sort((a, b) => a.IsDirectory == b.IsDirectory ? comparer.Compare(a.Name, b.Name) : a.IsDirectory ? -1 : 1);
        foreach (var node in list.Where(n => n.IsDirectory))
        {
            Finish(node.Children, comparer);
            node.Status = Aggregate(node);
        }
    }

    private static FileStatus Aggregate(FolderNode node)
    {
        if (!node.LeftExists) return FileStatus.RightOnly;
        if (!node.RightExists) return FileStatus.LeftOnly;
        if (node.Children.Count == 0)
            return node.File?.Status == FileStatus.Pending ? FileStatus.Pending : node.Status is FileStatus.Pending ? FileStatus.Pending : FileStatus.Equal;
        if (node.Children.Any(c => c.Status == FileStatus.Error)) return FileStatus.Error;
        if (node.Children.Any(c => c.Status == FileStatus.Pending)) return FileStatus.Pending;
        if (node.Children.Any(c => c.Status != FileStatus.Equal)) return FileStatus.Modified;
        return FileStatus.Equal;
    }

    public IReadOnlyList<FolderNode> Visible(int filter, FolderNameFilter nameFilter)
    {
        var rows = new List<FolderNode>();
        var matches = new Dictionary<FolderNode, bool>();
        bool searching = !nameFilter.IsEmpty;
        bool Matches(FolderNode node)
        {
            if (matches.TryGetValue(node, out bool value)) return value;
            bool match;
            if (node.IsDirectory)
            {
                // Unscanned / empty dirs still appear so the root wave is visible immediately.
                bool childMatch = node.Children.Any(Matches);
                match = node.Children.Count == 0
                    ? filter switch
                    {
                        1 => node.Status is not FileStatus.Equal,
                        2 => node.Status == FileStatus.Equal,
                        3 => node.Status == FileStatus.LeftOnly,
                        4 => node.Status == FileStatus.RightOnly,
                        _ => true,
                    }
                    : childMatch;
                if (match && searching)
                    match = nameFilter.Matches(node.RelativePath, node.Name) || childMatch;
            }
            else
            {
                match = (filter switch
                {
                    1 => node.Status is not FileStatus.Equal,
                    2 => node.Status == FileStatus.Equal,
                    3 => node.Status == FileStatus.LeftOnly,
                    4 => node.Status == FileStatus.RightOnly,
                    _ => true,
                }) && (!searching || nameFilter.Matches(node.RelativePath, node.Name));
            }
            matches[node] = match; return match;
        }
        void Add(IEnumerable<FolderNode> list)
        {
            foreach (var node in list)
            {
                if (!Matches(node)) continue;
                rows.Add(node);
                if (node.IsDirectory && (node.IsExpanded || searching)) Add(node.Children);
            }
        }
        Add(Roots); return rows;
    }
    private static (string Size, string Modified) Metadata(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return ("—", Controls.Lang.Get("file_unreadable"));
            long size = file.Length;
            return (size < 1024 ? $"{size} B" : size < 1024 * 1024 ? $"{size / 1024d:0.0} KB" : $"{size / 1024d / 1024d:0.0} MB", file.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ("—", Controls.Lang.Get("file_unreadable")); }
    }
}
