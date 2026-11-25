using System.Text.Json;
using ModularDiff.Contracts;
using ModularDiff.Core;

/// <summary>目录比较的契约测试:最终结果、进度增量、报告输出。
/// 背景:目录模块改为渐进式 BFS 扫描后,结果里出现了目录条目与 Pending 状态 ——
/// 任何把"Files 的每一项都当文件 / Status != Equal 即差异"的消费者都会被影响。</summary>
internal static class DirectoryScanTests
{
    /// <summary>同步、线程安全的进度记录器。不能用 Progress&lt;T&gt;:它把回调异步投递到线程池,
    /// 比较任务返回时回调未必送达,测试线程读到空集合就会间歇性失败;而且进度来自多个工作线程,
    /// 无锁的 List 写入本身也不安全。</summary>
    internal sealed class RecordingProgress : IProgress<ComparisonProgress>
    {
        private readonly object _gate = new();
        public List<ComparisonProgress> Reports { get; } = [];
        public ComparisonResult? RootWave { get; private set; }
        public List<FileComparison> Added { get; } = [];
        public List<FileComparison> Updates { get; } = [];
        public HashSet<string> Announced { get; } = new(StringComparer.Ordinal);
        /// <summary>RelativePath → index of the report that announced the entry.</summary>
        public Dictionary<string, int> AnnounceIndex { get; } = new(StringComparer.Ordinal);
        /// <summary>RelativePath → index of the first FileUpdate report for it.</summary>
        public Dictionary<string, int> FirstUpdateIndex { get; } = new(StringComparer.Ordinal);
        public void Report(ComparisonProgress value)
        {
            lock (_gate)
            {
                int index = Reports.Count;
                Reports.Add(value);
                if (value.PartialResult is { } partial && RootWave is null)
                {
                    RootWave = partial;
                    foreach (var f in partial.Files) { Announced.Add(f.RelativePath); AnnounceIndex.TryAdd(f.RelativePath, index); }
                }
                if (value.Added is { } added)
                    foreach (var f in added) { Added.Add(f); Announced.Add(f.RelativePath); AnnounceIndex.TryAdd(f.RelativePath, index); }
                if (value.FileUpdate is { } update) { Updates.Add(update); FirstUpdateIndex.TryAdd(update.RelativePath, index); }
            }
        }
    }

    /// <summary>抛异常的进度接收者:模拟有缺陷的 UI 回调。</summary>
    private sealed class ThrowingProgress : IProgress<ComparisonProgress>
    {
        public void Report(ComparisonProgress value) => throw new InvalidOperationException("progress sink exploded");
    }

    public static async Task Run(ComparisonEngine engine, IPluginResolver plugins, string temp, Func<string, Func<Task>, Task> test)
    {
        string NewRoot(string name)
        {
            string root = Path.Combine(temp, "scan-" + name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }
        void Put(string root, string relative, string text)
        {
            string path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        static string Entry(ComparisonResult result, string relative) =>
            result.Files.SingleOrDefault(f => f.RelativePath == relative)?.Status.ToString() ?? "<missing>";

        await test("Directory compare: final result never carries Pending and rolls both-side folders up", async () =>
        {
            string root = NewRoot("rollup"), left = Path.Combine(root, "L"), right = Path.Combine(root, "R");
            Directory.CreateDirectory(left); Directory.CreateDirectory(right);
            Put(left, "same.txt", "a"); Put(right, "same.txt", "a");
            Put(left, "diff.txt", "x"); Put(right, "diff.txt", "y");
            Directory.CreateDirectory(Path.Combine(left, "emptydir")); Directory.CreateDirectory(Path.Combine(right, "emptydir"));
            Put(left, Path.Combine("clean", "x.txt"), "1"); Put(right, Path.Combine("clean", "x.txt"), "1");
            Put(left, Path.Combine("dirty", "y.txt"), "1"); Put(right, Path.Combine("dirty", "y.txt"), "2");
            Put(left, Path.Combine("broken", "locked.txt"), "1"); Put(right, Path.Combine("broken", "locked.txt"), "1");
            Put(left, Path.Combine("onlyL", "z.txt"), "z"); Put(right, Path.Combine("onlyR", "w.txt"), "w");

            // 左侧 broken/locked.txt 被独占打开:读它的比较必须降级为 Error 条目(FileShare.None 在
            // Windows 是共享冲突,在 Unix 由 .NET 用 flock 模拟 —— 两个平台都确定可复现)。
            using (var held = new FileStream(Path.Combine(left, "broken", "locked.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var result = await engine.CompareAsync(new(left, right, "myers"));
                var pending = result.Files.Where(f => f.Status == FileStatus.Pending).Select(f => f.RelativePath).ToArray();
                Check(pending.Length == 0, "Final result carries Pending entries: " + string.Join(", ", pending));
                Check(result.Files.Where(f => f.IsDirectory).All(f => f.Status != FileStatus.Pending), "A both-side folder stays Pending forever.");

                Check(Entry(result, "emptydir") == "Equal", "empty both-side folder should be Equal: " + Entry(result, "emptydir"));
                Check(Entry(result, "clean") == "Equal", "folder with only equal files should be Equal: " + Entry(result, "clean"));
                Check(Entry(result, "dirty") == "Modified", "folder containing a differing file should roll up to Modified: " + Entry(result, "dirty"));
                Check(Entry(result, "broken") == "Error", "folder containing an error should roll up to Error: " + Entry(result, "broken"));
                Check(Entry(result, "onlyL") == "LeftOnly" && Entry(result, "onlyR") == "RightOnly", "one-side folders keep their side status.");
                Check(Entry(result, "broken/locked.txt".Replace('/', Path.DirectorySeparatorChar)) == "Error", "locked file must be an Error entry, not a crash.");

                var files = result.Files.Where(f => !f.IsDirectory).ToArray();
                Check(files.Length == 7, $"7 files expected, got {files.Length}");
                Check(files.Count(f => f.Status == FileStatus.Modified) == 2 && files.Count(f => f.Status == FileStatus.LeftOnly) == 1
                    && files.Count(f => f.Status == FileStatus.RightOnly) == 1 && files.Count(f => f.Status == FileStatus.Equal) == 2
                    && files.Count(f => f.Status == FileStatus.Error) == 1, "file statuses wrong after the rewrite");
                Check(result.HasDifferences && result.HasErrors, "result flags");
            }
        });

        await test("Directory compare: progress deltas survive wave throttling; every nested entry is announced", async () =>
        {
            string root = NewRoot("waves"), left = Path.Combine(root, "L"), right = Path.Combine(root, "R");
            const int depth = 14;   // 每一层都是一个 BFS 波;快速机器上相邻波远快于 250ms 的节流窗口
            Put(left, "root.txt", "r"); Put(right, "root.txt", "r");
            string chain = "";
            for (int i = 1; i <= depth; i++)
            {
                chain = chain.Length == 0 ? $"d{i}" : Path.Combine(chain, $"d{i}");
                Put(left, Path.Combine(chain, $"f{i}.txt"), "L"); Put(right, Path.Combine(chain, $"f{i}.txt"), "R");
            }
            var recorder = new RecordingProgress();
            var result = await engine.CompareAsync(new(left, right, "myers"), progress: recorder);

            Check(recorder.RootWave is not null, "Root wave must be reported.");
            Check(recorder.RootWave!.Files.Any(f => !f.IsDirectory && f.RelativePath == "root.txt"), "Root file listed in the first wave.");
            Check(recorder.RootWave.Files.All(f => !f.RelativePath.Contains("f1.txt")), "A nested file leaked into the root wave.");
            // 契约:Added 是"后续增量,从不重发全表"。那么只应用 PartialResult + Added 的消费者最终必须看到全部条目。
            string[] unannounced = result.Files.Where(f => !recorder.Announced.Contains(f.RelativePath)).Select(f => f.RelativePath).ToArray();
            Check(unannounced.Length == 0, $"Throttled waves dropped {unannounced.Length} entries from Added deltas, e.g. {string.Join(", ", unannounced.Take(3))}");
            // 每个内容比较结果(FileUpdate)都必须对应一个已公布的条目,否则消费者无处落笔。
            string[] orphanUpdates = recorder.Updates.Where(u => !recorder.Announced.Contains(u.RelativePath)).Select(u => u.RelativePath).Distinct().ToArray();
            Check(orphanUpdates.Length == 0, $"FileUpdate arrived for entries never announced: {string.Join(", ", orphanUpdates.Take(3))}");
            Check(recorder.Updates.Count == depth + 1 && recorder.Updates.All(u => u.Status != FileStatus.Pending), "one resolved update per compared file");
            // 顺序保证:某个条目的 FileUpdate 永远不早于该条目的公布(PartialResult/Added)。
            // 否则"按增量应用"的消费者收到更新时无处落笔,文件要等最终全量结果才不再是"…"。
            string[] outOfOrder = recorder.FirstUpdateIndex
                .Where(kv => !recorder.AnnounceIndex.TryGetValue(kv.Key, out int at) || at > kv.Value)
                .Select(kv => $"{kv.Key} (update #{kv.Value})").ToArray();
            Check(outOfOrder.Length == 0, "FileUpdate reported before its entry was announced: " + string.Join(", ", outOfOrder.Take(4)));
            // 根波条目只经 PartialResult 公布,不得再作为 Added 重发(重复增量会让消费者把已解析状态改回占位)。
            string[] resent = recorder.Added.Where(a => recorder.RootWave!.Files.Any(r => r.RelativePath == a.RelativePath)).Select(a => a.RelativePath).Distinct().ToArray();
            Check(resent.Length == 0, "Root-wave entries were re-sent as Added deltas: " + string.Join(", ", resent.Take(3)));
            // 扫描报告的 Completed 是"迄今已发现的文件数"(界面用它显示"已发现 N 个文件")。
            var scanReports = recorder.Reports.Where(r => r.Path.StartsWith("scan:", StringComparison.Ordinal)).ToArray();
            Check(scanReports[^1].Completed == depth + 1, $"Last scan report should carry the file count ({depth + 1}), got {scanReports[^1].Completed}");
            Check(result.Files.Count(f => !f.IsDirectory) == depth + 1, "final inventory");
        });

        await test("Directory compare: a throwing progress sink never takes the compare down", async () =>
        {
            string root = NewRoot("throwing"), left = Path.Combine(root, "L"), right = Path.Combine(root, "R");
            Put(left, "a.txt", "1"); Put(right, "a.txt", "1");
            Put(left, Path.Combine("s", "b.txt"), "1"); Put(right, Path.Combine("s", "b.txt"), "2");
            ComparisonResult result;
            try { result = await engine.CompareAsync(new(left, right, "myers"), progress: new ThrowingProgress()); }
            catch (InvalidOperationException ex) { throw new Exception("A progress sink exception failed the compare: " + ex.Message); }
            Check(result.Files.Count(f => !f.IsDirectory) == 2 && result.Files.Any(f => f.Status == FileStatus.Modified), "compare result intact");
        });

        await test("Directory compare: cancellation stops promptly", async () =>
        {
            string root = NewRoot("cancel"), left = Path.Combine(root, "L"), right = Path.Combine(root, "R");
            Put(left, "a.txt", "1"); Put(right, "a.txt", "1");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool threw = false;
            try { await engine.CompareAsync(new(left, right, "myers"), cancellationToken: cancelled.Token); }
            catch (OperationCanceledException) { threw = true; }
            Check(threw, "A cancelled compare must throw OperationCanceledException.");
        });

        await test("Directory reports list one-side folders, never Pending, and count files only", async () =>
        {
            string root = NewRoot("report"), left = Path.Combine(root, "L"), right = Path.Combine(root, "R");
            Put(left, "same.txt", "a"); Put(right, "same.txt", "a");
            Put(left, "diff.txt", "x"); Put(right, "diff.txt", "y");
            Put(left, Path.Combine("sub", "deeper", "n.txt"), "n"); Put(right, Path.Combine("sub", "deeper", "n.txt"), "m");
            Put(left, Path.Combine("onlyL", "z.txt"), "z");

            var result = await engine.CompareAsync(new(left, right, "myers"));
            using var text = new StringWriter();
            await plugins.Get<IResultFormatter>("text").WriteAsync(result, text);
            string body = text.ToString();
            Check(!body.Contains("Pending"), "Text report shows a transient Pending state:\n" + body);
            Check(body.Contains("Files: 4; changed: 3; errors: 0"), "Text summary must count files, not folder rows:\n" + body);
            Check(body.Contains("[LeftOnly] onlyL/"), "Text report should list a folder that exists on one side only:\n" + body);
            // 逐行精确匹配:两侧都有的目录不应各占一行(宽松的 Contains 会误中 "sub/deeper/n.txt" 这样的合法行)
            string[] folderRows = body.Split('\n').Select(l => l.Trim())
                .Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\[\w+\] sub([/\\]deeper)?[/\\]?$")).ToArray();
            Check(folderRows.Length == 0, "Both-side folders are structural; their files are already listed: " + string.Join(" | ", folderRows));
            Check(body.Split('\n').Count(l => l.Trim().StartsWith("[Equal] same.txt")) == 1, "equal file listed once");

            using var html = new StringWriter();
            await plugins.Get<IResultFormatter>("html").WriteAsync(result, html);
            string page = html.ToString();
            Check(!page.Contains("Pending"), "HTML report shows Pending.");
            Check(page.Contains("4 个文件") && page.Contains("差异 3"), "HTML summary counts:\n" + page[..Math.Min(400, page.Length)]);
            Check(page.Contains("onlyL/"), "HTML report should list the one-side folder.");

            using var json = new StringWriter();
            await plugins.Get<IResultFormatter>("json").WriteAsync(result, json);
            using var parsed = JsonDocument.Parse(json.ToString());
            Check(!json.ToString().Contains("\"Pending\""), "JSON report carries Pending.");
            var folders = parsed.RootElement.GetProperty("Files").EnumerateArray().Where(f => f.GetProperty("IsDirectory").GetBoolean()).ToArray();
            Check(folders.Length == 3 && folders.All(f => f.GetProperty("Status").GetString() is "Equal" or "Modified" or "LeftOnly"),
                "JSON keeps folder entries with a resolved status for tree views.");
        });
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
