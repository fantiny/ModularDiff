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

        // A faulty progress sink must never fail the comparison itself.
        void Report(ComparisonProgress value)
        {
            try { progress?.Report(value); }
            catch { /* the sink's problem, not the compare's */ }
        }

        // Entries reach a consumer through PartialResult (root wave) or Added (later waves). A
        // FileUpdate is only sent once its entry was announced, so a consumer that applies deltas in
        // order always has somewhere to put it; a result that arrives early is held until then.
        // The "announced" flag is set only AFTER the announcement was delivered: setting it first
        // would let a compare worker slip its FileUpdate in ahead of the entry's own announcement.
        var announced = new ConcurrentDictionary<string, byte>(comparer);
        var heldUpdates = new ConcurrentDictionary<string, FileComparison>(comparer);
        // A path discovered twice (a file and a folder of the same name, a folder that fails to
        // enumerate) ends up replaced. A consumer that only applies deltas was told the first
        // version, so the final one goes out as an update once the entry has been announced.
        // The numbers describe what an update belongs to: content compares for a file. A folder's answer belongs to the
        // scan (Total 0, as the scan reports'), so a UI does not read it as "comparing content n/m · folder".
        ComparisonProgress UpdateReport(FileComparison update) => update.IsDirectory
            ? new(Volatile.Read(ref filesFound), 0, update.RelativePath, FileUpdate: update)
            : new(Volatile.Read(ref compareDone), Math.Max(Volatile.Read(ref compareTotal), Volatile.Read(ref compareDone)), update.RelativePath, FileUpdate: update);
        void Replace(FileComparison entry)
        {
            inventory[entry.RelativePath] = entry;
            if (!announced.ContainsKey(entry.RelativePath)) { heldUpdates[entry.RelativePath] = entry; return; }   // goes out right after its announcement
            Report(UpdateReport(entry));
        }
        void Delivered(IReadOnlyList<FileComparison> entries)
        {
            foreach (var entry in entries) announced.TryAdd(entry.RelativePath, 0);
            foreach (var entry in entries)
                if (heldUpdates.TryRemove(entry.RelativePath, out var update))
                    Report(UpdateReport(update));
        }
        FileComparison[] Ordered() => inventory.Values.OrderBy(f => f.RelativePath, comparer).ThenBy(f => f.Status).ToArray();
        ComparisonResult Snapshot() => new(request.LeftPath, request.RightPath, Id, request.AlgorithmId, Ordered(), []);

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
                Interlocked.Increment(ref compareDone);
                if (announced.ContainsKey(pair.Relative)) Report(UpdateReport(item));
                else
                {
                    heldUpdates[pair.Relative] = item;
                    if (announced.ContainsKey(pair.Relative) && heldUpdates.TryRemove(pair.Relative, out _))
                        Report(UpdateReport(item));   // announced while we were holding it
                }
            });
        }, cancellationToken);

        // BFS: root wave sends PartialResult once; later waves send only Added deltas. Entries found
        // in a throttled wave carry over to the next reported wave — a delta is never dropped.
        var pending = new ConcurrentQueue<string>();
        pending.Enqueue("");
        var unreported = new ConcurrentBag<FileComparison>();
        bool firstWave = true;
        var scanWorkers = Math.Clamp(Environment.ProcessorCount, 2, 8);
        long lastWaveReport = Environment.TickCount64;

        try
        {
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
                    var leftTask = Task.Run(() => Enumerate("left", leftDir, relative, comparer, token), token);
                    var rightTask = Task.Run(() => Enumerate("right", rightDir, relative, comparer, token), token);
                    await Task.WhenAll(leftTask, rightTask).ConfigureAwait(false);
                    var leftEntries = leftTask.Result;
                    var rightEntries = rightTask.Result;
                    var childKeys = new List<string>();   // what this listing found directly under the folder

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
                        else Replace(entry);
                        childKeys.Add(childRel);
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
                            entry = new(childRel, FileStatus.Pending, [], Note: "内容比较中…");
                        // The placeholder goes into the inventory BEFORE the pair is queued: the compare
                        // worker replaces inventory[relative] with its result, and a placeholder written
                        // afterwards would overwrite a result that is already in.
                        if (inventory.TryAdd(childRel, entry)) waveAdded.Add(entry);
                        else Replace(entry);
                        childKeys.Add(childRel);
                        if (leftFile is not null && rightFile is not null)
                        {
                            Interlocked.Increment(ref compareTotal);
                            await bothSides.Writer.WriteAsync((childRel, leftFile, rightFile), token).ConfigureAwait(false);
                        }
                        Interlocked.Increment(ref filesFound);
                    }

                    foreach (var err in leftEntries.Errors.Concat(rightEntries.Errors))
                    {
                        if (inventory.TryAdd(err.RelativePath, err)) waveAdded.Add(err);
                        else Replace(err);
                        if (err.RelativePath != relative) childKeys.Add(err.RelativePath);   // an error of the folder itself is not a child
                    }

                    if (relative.Length > 0 && inventory.TryGetValue(relative, out var parent) && parent.IsDirectory
                        && parent.Status == FileStatus.Pending)
                    {
                        // The folder's own listing is in: it is no longer "being scanned". Its entry now says what was
                        // found under it, rolled up as the final result rolls it up (Equal for nothing). An entry
                        // announced as Pending is answered only when that is news: while files inside are still being
                        // compared it stays Pending, and what is found under it arrives with those files. Without the
                        // answer, a folder with nothing pending under it — an empty one above all — would say
                        // "scanning…" until the final result.
                        FileStatus rolled = FileStatus.Equal;
                        foreach (string child in childKeys)
                            if (inventory.TryGetValue(child, out var childEntry)) rolled = RollUp(rolled, childEntry.Status);
                        var resolved = parent with { Status = rolled, Note = null };
                        if (rolled == FileStatus.Pending) inventory[relative] = resolved;
                        else Replace(resolved);
                    }
                });

                foreach (string dir in nextDirs.Keys) pending.Enqueue(dir);
                foreach (var entry in waveAdded) unreported.Add(entry);

                int found = Volatile.Read(ref filesFound);
                long now = Environment.TickCount64;
                if (firstWave)
                {
                    lastWaveReport = now;
                    var root = Snapshot();
                    Report(new(found, 0, "scan:root", PartialResult: root));
                    Delivered(root.Files);
                    unreported.Clear();   // the root wave was announced in full; its entries are not deltas
                }
                else if (!unreported.IsEmpty && (now - lastWaveReport >= 250 || pending.IsEmpty))
                {
                    // Later waves are throttled to keep progress.Report cheap for the UI.
                    lastWaveReport = now;
                    var added = unreported.ToArray();
                    unreported.Clear();
                    Report(new(found, 0, $"scan:wave:{found}", Added: added));
                    Delivered(added);
                }
                firstWave = false;
                if (!request.EffectiveOptions.Recursive) break;
            }
        }
        catch
        {
            // The scan failed or was cancelled mid-way: close the gate and let the compare workers
            // wind down, so they do not live on as a task nobody awaits. The original exception wins.
            bothSides.Writer.TryComplete();
            try { await compareTask.ConfigureAwait(false); } catch { }
            throw;
        }

        bothSides.Writer.TryComplete();
        await compareTask.ConfigureAwait(false);
        foreach (var update in heldUpdates.Values) Report(UpdateReport(update));
        return new(request.LeftPath, request.RightPath, Id, request.AlgorithmId, ResolveFolders(Ordered(), comparer), []);
    }

    /// <summary>A folder's status from its children's: an error anywhere, else still pending, else differing, else
    /// equal — the order <see cref="ResolveFolders"/> rolls up with.</summary>
    private static FileStatus RollUp(FileStatus soFar, FileStatus child) =>
        soFar == FileStatus.Error || child == FileStatus.Error ? FileStatus.Error
        : soFar == FileStatus.Pending || child == FileStatus.Pending ? FileStatus.Pending
        : soFar != FileStatus.Equal || child != FileStatus.Equal ? FileStatus.Modified
        : FileStatus.Equal;

    /// <summary>The final result never carries <see cref="FileStatus.Pending"/>. A folder present on
    /// both sides summarises its subtree the way a tree view rolls statuses up: a differing or
    /// one-sided file inside makes it Modified, an error inside makes it Error, otherwise Equal.
    /// Consumers that only care about files can still filter on <c>IsDirectory</c>.</summary>
    private static FileComparison[] ResolveFolders(FileComparison[] entries, StringComparer comparer)
    {
        var rollup = new Dictionary<string, FileStatus>(comparer);
        foreach (var entry in entries)
            if (entry.IsDirectory && entry.Status == FileStatus.Pending) rollup[entry.RelativePath] = FileStatus.Equal;
        if (rollup.Count == 0) return entries;
        foreach (var entry in entries)
        {
            if (entry.IsDirectory || entry.Status == FileStatus.Equal) continue;
            for (string? folder = Path.GetDirectoryName(entry.RelativePath); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
                if (rollup.TryGetValue(folder, out var current))
                    rollup[folder] = entry.Status == FileStatus.Error ? FileStatus.Error
                        : current == FileStatus.Error ? FileStatus.Error : FileStatus.Modified;
        }
        return [.. entries.Select(entry => entry.IsDirectory && rollup.TryGetValue(entry.RelativePath, out var status) ? entry with { Status = status } : entry)];
    }

    private sealed class DirListing(StringComparer comparer)
    {
        public Dictionary<string, string> Files { get; } = new(comparer);
        public Dictionary<string, string> Dirs { get; } = new(comparer);
        public List<FileComparison> Errors { get; } = [];
    }

    private static DirListing Enumerate(string side, string directory, string relativePrefix, StringComparer comparer, CancellationToken token)
    {
        var listing = new DirListing(comparer);
        if (!Directory.Exists(directory)) return listing;
        try
        {
            // Never descend through a link: the other side may have a real folder of the same name,
            // which alone puts this path in the scan queue.
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return listing;
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
                            Note: $"{side}: symbolic link/reparse point excluded; comparison is incomplete."));
                        continue;
                    }
                    if ((attributes & FileAttributes.Directory) != 0) listing.Dirs[name] = path;
                    else listing.Files[name] = path;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    string name = Path.GetFileName(path);
                    string childRel = relativePrefix.Length == 0 ? name : Path.Combine(relativePrefix, name);
                    listing.Errors.Add(new(childRel, FileStatus.Error, [], Note: $"{side}: {ex.Message}"));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            listing.Errors.Add(new(relativePrefix.Length == 0 ? "." : relativePrefix, FileStatus.Error, [], Note: $"{side}: {ex.Message}"));
        }
        return listing;
    }
}
