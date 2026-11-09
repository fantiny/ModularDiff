namespace ModularDiff.Contracts;

// This assembly is shared with the default load context: plugins must not load a private copy.
public interface IPlugin
{
    string Id { get; }
    string DisplayName { get; }
    int ApiVersion => 1;
    IReadOnlyList<string> Dependencies => [];
}

public interface IPluginResolver
{
    T Get<T>(string id) where T : class, IPlugin;
    IReadOnlyList<T> GetAll<T>() where T : class, IPlugin;
}

public enum EditKind { Equal, Delete, Insert }
public sealed record Edit(EditKind Kind, int? LeftIndex, int? RightIndex);
public sealed record AlgorithmResult(IReadOnlyList<Edit> Edits, bool IsApproximate = false, string? Note = null);

public interface IDiffAlgorithm : IPlugin
{
    AlgorithmResult Compare(IReadOnlyList<string> left, IReadOnlyList<string> right,
        IPluginResolver plugins, CancellationToken cancellationToken = default);
}
// 行拆分插件:差异对齐页右键"换行"按各自领域规则把一行拆成多行(如 CSS 多属性行拆成每属性一行)。
public interface ILineSplitter : IPlugin
{
    bool CanSplit(string fileName, string line);
    IReadOnlyList<string> Split(string line);
}

public sealed record ComparisonOptions(
    bool IgnoreCase = false, bool IgnoreWhitespace = false, bool IgnoreLineEndings = false,
    bool Recursive = true, long MaxTextBytes = 8 * 1024 * 1024);
public sealed record ComparisonRequest(string LeftPath, string RightPath, string AlgorithmId = "myers",
    ComparisonOptions? Options = null)
{
    public ComparisonOptions EffectiveOptions => Options ?? new();
}
public enum FileStatus { Equal, Modified, LeftOnly, RightOnly, Error }
public sealed record DiffLine(EditKind Kind, int? LeftLine, int? RightLine,
    string? LeftText, string? RightText, string? LeftEnding, string? RightEnding);
public sealed record FileComparison(string RelativePath, FileStatus Status, IReadOnlyList<DiffLine> Lines,
    bool IsBinary = false, bool IsApproximate = false, string? Note = null);
public sealed record ComparisonResult(string LeftPath, string RightPath, string ModuleId, string AlgorithmId,
    IReadOnlyList<FileComparison> Files, IReadOnlyList<string> Warnings)
{
    public bool HasDifferences => Files.Any(f => f.Status is FileStatus.Modified or FileStatus.LeftOnly or FileStatus.RightOnly);
    public bool HasErrors => Files.Any(f => f.Status == FileStatus.Error);
}
public sealed record ComparisonProgress(int Completed, int Total, string Path);

public interface IComparisonModule : IPlugin
{
    bool CanCompare(string leftPath, string rightPath);
    Task<ComparisonResult> CompareAsync(ComparisonRequest request, IPluginResolver plugins,
        IProgress<ComparisonProgress>? progress = null, CancellationToken cancellationToken = default);
}

public interface IResultFormatter : IPlugin
{
    string FileExtension { get; }
    Task WriteAsync(ComparisonResult result, TextWriter writer, CancellationToken cancellationToken = default);
}

// A UI-neutral presentation extension: neither WPF types nor algorithm implementation details leak in.
public sealed record HighlightSpan(int Start, int Length);
public sealed record TextCell(int LineNumber, string Text, string Ending, IReadOnlyList<HighlightSpan> Highlights);
public sealed record AlignedRow(int SourceIndex, TextCell? Left, TextCell? Right, int HunkId = -1, int HiddenLines = 0)
{
    public bool IsDifferent => HunkId >= 0;
}
public sealed record DiffHunk(int Id, int StartRow, int EndRow);
public sealed record TextPresentation(IReadOnlyList<AlignedRow> Rows, IReadOnlyList<DiffHunk> Hunks, int TotalRows);
public interface ITextPresentation : IPlugin
{
    // contextLines: -1 = all, 0 = changes only, >0 = context around changed blocks.
    TextPresentation Build(IReadOnlyList<DiffLine> lines, int contextLines = -1, CancellationToken cancellationToken = default);
}
