using System.ComponentModel;
using System.IO;
using System.Windows;
using ModularDiff.Contracts;
using ModularDiff.Core;

namespace ModularDiff.Gui;

public sealed class FolderNode : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public required string Name { get; init; }
    public required string RelativePath { get; init; }
    // A row shows a node through bindings, and the pooled row lists keep a row's visuals and its data
    // context while the node stays at that position. A node that is patched in place — a file resolves,
    // a folder is expanded — has to say what changed, or its row goes on showing the old state. Nothing
    // is allocated or raised while no row shows the node, so building a tree costs nothing more.
    private bool _isDirectory;
    public bool IsDirectory
    {
        get => _isDirectory;
        set { if (value == _isDirectory) return; _isDirectory = value; Changed([nameof(Symbol), nameof(KindIcon)]); }
    }
    public int Depth { get; init; }
    /// <summary>Collapsed by default so huge trees only paint the top level until expanded.</summary>
    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (value == _isExpanded) return; _isExpanded = value; Changed([nameof(Symbol)]); }
    }
    private bool _leftExists;
    public bool LeftExists
    {
        get => _leftExists;
        set { if (value == _leftExists) return; _leftExists = value; Changed([nameof(LeftExists), nameof(LeftName)]); }
    }
    private bool _rightExists;
    public bool RightExists
    {
        get => _rightExists;
        set { if (value == _rightExists) return; _rightExists = value; Changed([nameof(RightExists), nameof(RightName)]); }
    }
    private FileComparison? _file;
    public FileComparison? File
    {
        get => _file;
        set { if (ReferenceEquals(value, _file)) return; _file = value; Changed([nameof(Description)]); }
    }
    private FileStatus _status;
    public FileStatus Status
    {
        get => _status;
        set { if (value == _status) return; _status = value; Changed([nameof(Status), nameof(StatusGlyph), nameof(StatusText), nameof(Color), nameof(Description)]); }
    }
    public List<FolderNode> Children { get; } = [];
    public FolderNode? Parent { get; set; }
    private string? _normalizedPath;
    /// <summary>RelativePath with '/' separators — the form the name filter matches. Every
    /// keystroke in the filter box evaluates the whole tree, so it is built once per node.</summary>
    public string NormalizedPath => _normalizedPath ??= RelativePath.Replace('\\', '/');
    private string _leftSize = "", _rightSize = "", _leftModified = "", _rightModified = "";
    public string LeftSize { get => _leftSize; set { if (value == _leftSize) return; _leftSize = value; Changed([nameof(LeftSize)]); } }
    public string RightSize { get => _rightSize; set { if (value == _rightSize) return; _rightSize = value; Changed([nameof(RightSize)]); } }
    public string LeftModified { get => _leftModified; set { if (value == _leftModified) return; _leftModified = value; Changed([nameof(LeftModified)]); } }
    public string RightModified { get => _rightModified; set { if (value == _rightModified) return; _rightModified = value; Changed([nameof(RightModified)]); } }
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

    /// <summary>Names the properties the rows bind to.</summary>
    private void Changed(scoped ReadOnlySpan<string> names)
    {
        if (PropertyChanged is null) return;
        foreach (string name in names) PropertyChanged(this, new(name));
    }
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
        // The folder itself could not be listed. That is an error, not merely "different": "different"
        // suggests files to copy, and the files under it are only the side that could be read.
        if (node.File?.Status == FileStatus.Error) return FileStatus.Error;
        if (node.Children.Count == 0)
            return node.File?.Status == FileStatus.Pending ? FileStatus.Pending : node.Status is FileStatus.Pending ? FileStatus.Pending : FileStatus.Equal;
        if (node.Children.Any(c => c.Status == FileStatus.Error)) return FileStatus.Error;
        if (node.Children.Any(c => c.Status == FileStatus.Pending)) return FileStatus.Pending;
        if (node.Children.Any(c => c.Status != FileStatus.Equal)) return FileStatus.Modified;
        return FileStatus.Equal;
    }

    private readonly List<FolderNode> _rows = [];

    /// <summary>The nodes to show under a status filter and a name filter, in tree order. A
    /// folder with content is shown when a descendant is; a collapsed one hides its children
    /// unless a name filter is on, which opens every folder. Runs once per keystroke in the
    /// filter box, so it visits each node at most once and allocates nothing; the returned
    /// list is reused by the next call.</summary>
    public IReadOnlyList<FolderNode> Visible(int filter, FolderNameFilter nameFilter)
    {
        _rows.Clear();
        bool searching = !nameFilter.IsEmpty;
        bool Passes(FolderNode node) => filter switch
        {
            1 => node.Status is not FileStatus.Equal,
            2 => node.Status == FileStatus.Equal,
            3 => node.Status == FileStatus.LeftOnly,
            4 => node.Status == FileStatus.RightOnly,
            _ => true,
        };
        bool Belongs(FolderNode node) =>
            Passes(node) && (!searching || nameFilter.MatchesNormalized(node.NormalizedPath, node.Name));
        // Returns whether the node belongs; the rows it contributes are in place when it does.
        bool Visit(FolderNode node)
        {
            if (!node.IsDirectory || node.Children.Count == 0)
            {
                // Unscanned / empty dirs still appear so the root wave is visible immediately.
                if (!Belongs(node)) return false;
                _rows.Add(node);
                return true;
            }
            // The folder's row goes first and is taken back if no descendant belongs; the
            // descendants stay in the list only while the folder shows its children.
            int start = _rows.Count;
            _rows.Add(node);
            bool open = node.IsExpanded || searching, descendant = false;
            foreach (var child in node.Children)
                if (Visit(child)) { descendant = true; if (!open) break; }
            if (!descendant) { _rows.RemoveAt(start); return false; }
            if (!open) _rows.RemoveRange(start + 1, _rows.Count - start - 1);
            return true;
        }
        foreach (var root in Roots) Visit(root);
        return _rows;
    }

    /// <summary>The folder view's status line, counted from what the tree holds. While a scan runs the result
    /// the window was handed is the root wave alone; the tree is what the user sees. It counts what the result
    /// counts — every entry that is not a folder. An unreadable folder is such an entry: what the scan lists
    /// for it is its error. A folder with an error inside is not: its entry is a folder's, whatever its status.</summary>
    public (int Files, int Equal, int Different, int Errors, int Pending) Summarize()
    {
        int equal = 0, different = 0, errors = 0, pending = 0;
        foreach (var node in All)
        {
            if (node.File?.IsDirectory != false) continue;
            switch (node.Status)
            {
                case FileStatus.Equal: equal++; break;
                case FileStatus.Modified or FileStatus.LeftOnly or FileStatus.RightOnly: different++; break;
                case FileStatus.Error: errors++; break;
                default: pending++; break;
            }
        }
        return (equal + different + errors + pending, equal, different, errors, pending);
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
