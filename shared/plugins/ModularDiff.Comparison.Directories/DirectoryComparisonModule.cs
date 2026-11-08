using System.Collections.Concurrent;
using ModularDiff.Contracts;

namespace ModularDiff.Comparison.Directories;

public sealed class DirectoryComparisonModule : IComparisonModule
{
    public string Id => "directories";
    public string DisplayName => "Directories · recursive";
    public IReadOnlyList<string> Dependencies => ["files"];
    public bool CanCompare(string leftPath, string rightPath) => Directory.Exists(leftPath) && Directory.Exists(rightPath);

    public async Task<ComparisonResult> CompareAsync(ComparisonRequest request, IPluginResolver plugins,
        IProgress<ComparisonProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var fileModule = plugins.Get<IComparisonModule>("files");
        var files = new ConcurrentBag<FileComparison>();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var left = Scan(request.LeftPath, "left"); var right = Scan(request.RightPath, "right");
        var paths = left.Keys.Union(right.Keys, comparer).Order(comparer).ToArray();
        int completed = 0;
        await Parallel.ForEachAsync(paths, new ParallelOptions
        {
            CancellationToken = cancellationToken, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 4)
        }, async (relative, token) =>
        {
            FileComparison item;
            if (!left.TryGetValue(relative, out var a)) item = new(relative, FileStatus.RightOnly, []);
            else if (!right.TryGetValue(relative, out var b)) item = new(relative, FileStatus.LeftOnly, []);
            else
            {
                try
                {
                    var result = await fileModule.CompareAsync(request with { LeftPath = a, RightPath = b }, plugins, cancellationToken: token);
                    // Directory results retain summaries; GUI loads a selected file's edits lazily.
                    item = result.Files.Single() with { RelativePath = relative, Lines = [] };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { item = new(relative, FileStatus.Error, [], Note: ex.Message); }
            }
            files.Add(item);
            progress?.Report(new(Interlocked.Increment(ref completed), paths.Length, relative));
        });
        return new(request.LeftPath, request.RightPath, Id, request.AlgorithmId,
            files.OrderBy(f => f.RelativePath, comparer).ThenBy(f => f.Status).ToArray(), []);

        Dictionary<string, string> Scan(string root, string side)
        {
            var result = new Dictionary<string, string>(comparer);
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            files.Add(new(Path.GetRelativePath(root, path), FileStatus.Error, [],
                                Note: $"{side}: symbolic link/reparse point excluded; comparison is incomplete."));
                            continue;
                        }
                        if ((attributes & FileAttributes.Directory) != 0)
                        { if (request.EffectiveOptions.Recursive) pending.Push(path); }
                        else result.Add(Path.GetRelativePath(root, path), path);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { files.Add(new(Path.GetRelativePath(root, directory), FileStatus.Error, [], Note: $"{side}: {ex.Message}")); }
            }
            return result;
        }
    }
}
