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
// 代码格式化插件:把整侧文本按各语言规范重排(选择器/声明/花括号缩进等),⌗ 按钮调用。
public interface ICodeFormatter : IPlugin
{
    bool CanFormat(string fileName);
    IReadOnlyList<string> Format(IReadOnlyList<string> lines);
}

// 不重要差异规则:每条是一个正则,行等值判断前把匹配片段从行文本中剔除(如 $Id$、时间戳、版本号)。
public sealed record ComparisonOptions(
    bool IgnoreCase = false, bool IgnoreWhitespace = false, bool IgnoreLineEndings = false,
    bool Recursive = true, long MaxTextBytes = 8 * 1024 * 1024,
    IReadOnlyList<string>? UnimportantPatterns = null)
{
    public IReadOnlyList<string> EffectiveUnimportantPatterns => UnimportantPatterns ?? [];
}
public sealed record ComparisonRequest(string LeftPath, string RightPath, string AlgorithmId = "myers",
    ComparisonOptions? Options = null)
{
    public ComparisonOptions EffectiveOptions => Options ?? new();
}
public enum FileStatus { Equal, Modified, LeftOnly, RightOnly, Error }
public sealed record DiffLine(EditKind Kind, int? LeftLine, int? RightLine,
    string? LeftText, string? RightText, string? LeftEnding, string? RightEnding);
// 二进制差异区间:不等的连续字节段(相邻段间隔不超过 16 字节时合并,段数封顶防大文件膨胀)。
public sealed record ByteDiff(long Offset, long Length);
public sealed record FileComparison(string RelativePath, FileStatus Status, IReadOnlyList<DiffLine> Lines,
    bool IsBinary = false, bool IsApproximate = false, string? Note = null,
    IReadOnlyList<ByteDiff>? BinaryDiffs = null);
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
/// <summary>Manual alignment hint: force these two lines to be paired in the presentation.</summary>
public sealed record AlignmentHint(int LeftLine, int RightLine);

public interface ITextPresentation : IPlugin
{
    // contextLines: -1 = all, 0 = changes only, >0 = context around changed blocks.
    // hints: forced alignment pairs (user-driven "Align With"); isolatedLines are excluded from matching.
    TextPresentation Build(IReadOnlyList<DiffLine> lines, int contextLines = -1,
        CancellationToken cancellationToken = default,
        IReadOnlyList<AlignmentHint>? hints = null,
        IReadOnlyList<int>? isolatedLines = null);
}
