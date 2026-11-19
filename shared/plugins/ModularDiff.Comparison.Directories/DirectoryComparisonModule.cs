using System.Collections.Concurrent;
using System.Threading.Channels;
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
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var inventory = new ConcurrentDictionary<string, FileComparison>(comparer);
        var bothSides = Channel.CreateUnbounded<(string Relative, string Left, string Right)>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
        int compareTotal = 0, compareDone = 0, filesFound = 0;

        ComparisonResult Snapshot() => new(request.LeftPath, request.RightPath, Id, request.AlgorithmId,
            inventory.Values.OrderBy(f => f.RelativePath, comparer).ThenBy(f => f.Status).ToArray(), []);

        // Content compare overlaps BFS scan. Only FileUpdate is reported (no full Snapshot) so
        // the UI thread is not flooded with million-entry arrays.
        var compareWorkers = Math.Clamp(Environment.ProcessorCount, 1, 4);
        var compareTask = Task.Run(async () =>
        {
            await Parallel.ForEachAsync(bothSides.Reader.ReadAllAsync(cancellationToken), new ParallelOptions
            {
                CancellationToken = cancellationToken, MaxDegreeOfParallelism = compareWorkers
            }, async (pair, token) =>
            {
                FileComparison item;
                try
                {
                    var result = await fileModule.CompareAsync(
                        request with { LeftPath = pair.Left, RightPath = pair.Right }, plugins, cancellationToken: token);
                    item = result.Files.Single() with { RelativePath = pair.Relative, Lines = [], IsDirectory = false };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    item = new(pair.Relative, FileStatus.Error, [], Note: ex.GetBaseException().Message);
                }
                inventory[pair.Relative] = item;
                int done = Interlocked.Increment(ref compareDone);
                int total = Math.Max(Volatile.Read(ref compareTotal), done);
                progress?.Report(new(done, total, pair.Relative, FileUpdate: item));
            });
        }, cancellationToken);

        // BFS: root wave sends PartialResult once; later waves send only Added deltas.
        var pending = new ConcurrentQueue<string>();
        pending.Enqueue("");
        bool firstWave = true;
        var scanWorkers = Math.Clamp(Environment.ProcessorCount, 2, 8);
        long lastWaveReport = Environment.TickCount64;

        while (!pending.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wave = new List<string>();
            while (pending.TryDequeue(out var rel)) wave.Add(rel);
            if (wave.Count == 0) break;

            var nextDirs = new ConcurrentDictionary<string, byte>(comparer);
            var waveAdded = new ConcurrentBag<FileComparison>();
            await Parallel.ForEachAsync(wave, new ParallelOptions
            {
                CancellationToken = cancellationToken, MaxDegreeOfParallelism = scanWorkers
            }, async (relative, token) =>
            {
                string leftDir = relative.Length == 0 ? request.LeftPath : Path.Combine(request.LeftPath, relative);
                string rightDir = relative.Length == 0 ? request.RightPath : Path.Combine(request.RightPath, relative);
                var leftTask = Task.Run(() => Enumerate(leftDir, relative, comparer, token), token);
                var rightTask = Task.Run(() => Enumerate(rightDir, relative, comparer, token), token);
                await Task.WhenAll(leftTask, rightTask).ConfigureAwait(false);
                var leftEntries = leftTask.Result;
                var rightEntries = rightTask.Result;

                foreach (string name in leftEntries.Dirs.Keys.Union(rightEntries.Dirs.Keys, comparer))
                {
                    token.ThrowIfCancellationRequested();
                    string childRel = relative.Length == 0 ? name : Path.Combine(relative, name);
                    bool onLeft = leftEntries.Dirs.ContainsKey(name);
                    bool onRight = rightEntries.Dirs.ContainsKey(name);
                    FileStatus status = !onLeft ? FileStatus.RightOnly : !onRight ? FileStatus.LeftOnly
                        : request.EffectiveOptions.Recursive ? FileStatus.Pending : FileStatus.Equal;
                    var entry = new FileComparison(childRel, status, [], IsDirectory: true,
                        Note: status == FileStatus.Pending ? "子目录扫描中…" : null);
                    if (inventory.TryAdd(childRel, entry)) waveAdded.Add(entry);
                    else inventory[childRel] = entry;
                    if (request.EffectiveOptions.Recursive) nextDirs.TryAdd(childRel, 0);
                }

                foreach (string name in leftEntries.Files.Keys.Union(rightEntries.Files.Keys, comparer))
                {
                    token.ThrowIfCancellationRequested();
                    string childRel = relative.Length == 0 ? name : Path.Combine(relative, name);
                    leftEntries.Files.TryGetValue(name, out var leftFile);
                    rightEntries.Files.TryGetValue(name, out var rightFile);
                    FileComparison entry;
                    if (leftFile is null)
                        entry = new(childRel, FileStatus.RightOnly, []);
                    else if (rightFile is null)
                        entry = new(childRel, FileStatus.LeftOnly, []);
                    else
                    {
                        entry = new(childRel, FileStatus.Pending, [], Note: "内容比较中…");
                        Interlocked.Increment(ref compareTotal);
                        await bothSides.Writer.WriteAsync((childRel, leftFile, rightFile), token).ConfigureAwait(false);
                    }
                    if (inventory.TryAdd(childRel, entry)) waveAdded.Add(entry);
                    else inventory[childRel] = entry;
                    Interlocked.Increment(ref filesFound);
                }

                foreach (var err in leftEntries.Errors.Concat(rightEntries.Errors))
                {
                    if (inventory.TryAdd(err.RelativePath, err)) waveAdded.Add(err);
                    else inventory[err.RelativePath] = err;
                }

                if (relative.Length > 0 && inventory.TryGetValue(relative, out var parent) && parent.IsDirectory
                    && parent.Status == FileStatus.Pending)
                    inventory[relative] = parent with { Note = null };
            });

            foreach (string dir in nextDirs.Keys) pending.Enqueue(dir);

            int found = Volatile.Read(ref filesFound);
            long now = Environment.TickCount64;
            // Root always reports; later waves throttle to keep progress.Report cheap for the UI.
            bool report = firstWave || waveAdded.Count > 0 && (now - lastWaveReport >= 250 || pending.IsEmpty);
            if (report)
            {
                lastWaveReport = now;
                if (firstWave)
                    progress?.Report(new(found, 0, "scan:root", PartialResult: Snapshot()));
                else
                    progress?.Report(new(found, 0, $"scan:wave:{found}", Added: waveAdded.ToArray()));
            }
            firstWave = false;
            if (!request.EffectiveOptions.Recursive) break;
        }

        bothSides.Writer.TryComplete();
        await compareTask.ConfigureAwait(false);
        return Snapshot();
    }

    private static DirListing Enumerate(string directory, string relativePrefix, StringComparer comparer, CancellationToken token)
    {
        var listing = new DirListing(comparer);
        if (!Directory.Exists(directory)) return listing;
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var attributes = File.GetAttributes(path);
                    string name = Path.GetFileName(path);
                    string childRel = relativePrefix.Length == 0 ? name : Path.Combine(relativePrefix, name);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        listing.Errors.Add(new(childRel, FileStatus.Error, [],
                            Note: "symbolic link/reparse point excluded; comparison is incomplete."));
                        continue;
                    }
                    if ((attributes & FileAttributes.Directory) != 0) listing.Dirs[name] = path;
                    else listing.Files[name] = path;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    string name = Path.GetFileName(path);
                    string childRel = relativePrefix.Length == 0 ? name : Path.Combine(relativePrefix, name);
                    listing.Errors.Add(new(childRel, FileStatus.Error, [], Note: ex.Message));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            listing.Errors.Add(new(relativePrefix.Length == 0 ? "." : relativePrefix, FileStatus.Error, [], Note: ex.Message));
        }
        return listing;
    }

    private sealed class DirListing(StringComparer comparer)
    {
        public Dictionary<string, string> Files { get; } = new(comparer);
        public Dictionary<string, string> Dirs { get; } = new(comparer);
        public List<FileComparison> Errors { get; } = [];
    }
}
