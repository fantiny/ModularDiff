using ModularDiff.Contracts;
using ModularDiff.Core;

internal static class SyncTests
{
    public static async Task Run(PluginCatalog plugins, string temp, Func<string, Func<Task>, Task> test)
    {
        var planner = plugins.Get<ISyncPlanner>("sync-plan"); var executor = new TestSyncExecutor(plugins.Get<ISyncExecutor>("sync-execute"), new SyncSafetyContext(Path.Combine(temp,"sync-history"), plugins.Get<ISyncBackup>("sync-backup"), plugins.Get<ISyncJournal>("sync-journal"))); var preview = plugins.Get<ISyncPreview>("sync-preview");
        (string Left, string Right) Roots()
        {
            string root = Path.Combine(temp, Guid.NewGuid().ToString()); string a = Path.Combine(root, "left"), b = Path.Combine(root, "right");
            Directory.CreateDirectory(a); Directory.CreateDirectory(b); return (a, b);
        }
        Task<SyncResult> Execute(SyncPlan plan, CancellationToken token = default) => executor.ExecuteAsync(plan, plan.Items.Select(i => new SyncDecision(i.RelativePath, i.SuggestedAction)).ToArray(), cancellationToken: token);
        await test("Sync: read-only preview, binary overwrite, nested creation, target-only retention", async () =>
        {
            var (a,b) = Roots(); Directory.CreateDirectory(Path.Combine(a, "nested"));
            byte[] bytes = [0, 255, 10, 128, 1]; await File.WriteAllBytesAsync(Path.Combine(a, "nested", "binary"), bytes);
            await File.WriteAllTextAsync(Path.Combine(a, "changed"), "new"); await File.WriteAllTextAsync(Path.Combine(b, "changed"), "old"); await File.WriteAllTextAsync(Path.Combine(b, "keep"), "keep");
            var plan = await planner.PlanAsync(a, b, ["nested/binary", "changed", "keep"], SyncMode.LeftToRight);
            Check(!File.Exists(Path.Combine(b, "nested", "binary")) && await File.ReadAllTextAsync(Path.Combine(b, "changed")) == "old", "Preview wrote files");
            Check(preview.Describe(plan).Single(i => i.RelativePath == "changed").Choices.Any(c => c.Label.Contains("覆盖")), "Overwrite not visible");
            var result = await Execute(plan); Check(result.Entries.Count(i => i.Outcome == SyncOutcome.Copied) == 2, "Copy count");
            Check((await File.ReadAllBytesAsync(Path.Combine(b, "nested", "binary"))).SequenceEqual(bytes) && await File.ReadAllTextAsync(Path.Combine(b, "changed")) == "new" && File.Exists(Path.Combine(b, "keep")), "Wrong copy content");
            Check(!Directory.EnumerateFiles(b, ".modulardiff-sync-*", SearchOption.AllDirectories).Any(), "Temporary file leaked");
        });
        await test("Sync: bidirectional conflicts require choices; subset decisions never imply full batch", async () =>
        {
            var (a,b) = Roots(); await File.WriteAllTextAsync(Path.Combine(a, "conflict"), "left"); await File.WriteAllTextAsync(Path.Combine(b, "conflict"), "right");
            await File.WriteAllTextAsync(Path.Combine(a, "only-left"), "left"); await File.WriteAllTextAsync(Path.Combine(b, "only-right"), "right");
            var plan = await planner.PlanAsync(a, b, ["conflict", "only-left", "only-right"], SyncMode.Bidirectional);
            Check(plan.Items.Single(i => i.RelativePath == "conflict") is { Conflict: true, SuggestedAction: SyncAction.Skip }, "Conflict auto-selected");
            var result = await executor.ExecuteAsync(plan, [new("conflict", SyncAction.RightToLeft)]);
            Check(result.Entries.Count(i => i.Outcome == SyncOutcome.Copied) == 1 && await File.ReadAllTextAsync(Path.Combine(a, "conflict")) == "right", "Right to left failed");
            Check(!File.Exists(Path.Combine(b, "only-left")) && !File.Exists(Path.Combine(a, "only-right")), "Unselected file copied");
            plan = await planner.PlanAsync(a,b,["only-left","only-right"],SyncMode.Bidirectional); result = await Execute(plan);
            Check(result.Entries.All(i => i.Outcome == SyncOutcome.Copied), "Bidirectional additions");
        });
        await test("Sync: stale source, target, and newly created target are never overwritten", async () =>
        {
            foreach (int scenario in Enumerable.Range(0, 3))
            {
                var (a,b) = Roots(); await File.WriteAllTextAsync(Path.Combine(a, "file"), "source");
                if (scenario != 2) await File.WriteAllTextAsync(Path.Combine(b, "file"), "target");
                var plan = await planner.PlanAsync(a,b,["file"],SyncMode.LeftToRight);
                await File.WriteAllTextAsync(Path.Combine(scenario == 0 ? a : b, "file"), "external");
                var result = await Execute(plan); Check(result.Entries.Single().Outcome == SyncOutcome.Failed, "Stale plan accepted");
                Check(await File.ReadAllTextAsync(Path.Combine(b, "file")) == (scenario == 0 ? "target" : "external"), "External content overwritten");
            }
        });
        await test("Sync: traversal, overlapping roots and file/directory collisions blocked", async () =>
        {
            var (a,b) = Roots(); Directory.CreateDirectory(Path.Combine(b, "folder")); await File.WriteAllTextAsync(Path.Combine(a, "folder"), "file");
            var plan = await planner.PlanAsync(a,b,["../escape", Path.Combine(a, "outside"), "folder"],SyncMode.LeftToRight);
            Check(plan.Items.All(i => i.Problem is not null && i.SuggestedAction == SyncAction.Skip), "Unsafe item accepted");
            await Expect<IOException>(() => planner.PlanAsync(a,a,[],SyncMode.LeftToRight));
            await Expect<IOException>(() => planner.PlanAsync(a,Path.Combine(a,"nested"),[],SyncMode.LeftToRight));
            Check((await executor.ExecuteAsync(plan, plan.Items.Select(i => new SyncDecision(i.RelativePath,SyncAction.LeftToRight)).ToArray())).Entries.All(i => i.Outcome == SyncOutcome.Failed), "Executor ignored blocked item");
        });
        await test("Sync: cancellation and readonly targets preserve data and report partial results", async () =>
        {
            var (a,b) = Roots(); foreach(string name in new[]{"a","b"}) { await File.WriteAllTextAsync(Path.Combine(a,name),"source"); await File.WriteAllTextAsync(Path.Combine(b,name),"target"); }
            var plan = await planner.PlanAsync(a,b,["a","b"],SyncMode.LeftToRight);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Check((await Execute(plan,cancellation.Token)).Entries.All(i=>i.Outcome==SyncOutcome.Cancelled),"Cancellation");
            File.SetAttributes(Path.Combine(b,"a"), FileAttributes.ReadOnly);
            try { var result=await Execute(plan); Check(result.Entries[0].Outcome==SyncOutcome.Failed && result.Entries[1].Outcome==SyncOutcome.Copied,"Partial failure report"); Check(await File.ReadAllTextAsync(Path.Combine(b,"a"))=="target","Readonly target changed"); }
            finally { File.SetAttributes(Path.Combine(b,"a"), FileAttributes.Normal); }
        });
        await test("Sync: duplicate and unknown decisions rejected before writes; identical files skipped", async () =>
        {
            var (a,b)=Roots(); await File.WriteAllTextAsync(Path.Combine(a,"file"),"same"); await File.WriteAllTextAsync(Path.Combine(b,"file"),"same");
            var plan=await planner.PlanAsync(a,b,["file","file"],SyncMode.LeftToRight); Check(plan.Items.Count==1 && plan.Items[0].SuggestedAction==SyncAction.Skip,"Deduplication or equality");
            await Expect<ArgumentException>(()=>executor.ExecuteAsync(plan,[new("file",SyncAction.LeftToRight),new("file",SyncAction.RightToLeft)]));
            await Expect<ArgumentException>(()=>executor.ExecuteAsync(plan,[new("other",SyncAction.LeftToRight)]));
            Check((await Execute(plan)).Entries.Single().Outcome==SyncOutcome.Skipped,"Equal files copied");
        });
        await test("Sync: cancelling a running batch retains completed files and cancels the remainder", async () =>
        {
            var (a,b) = Roots();
            foreach (string name in new[] { "a", "b", "c" }) await File.WriteAllTextAsync(Path.Combine(a,name), name);
            var plan = await planner.PlanAsync(a,b,["a","b","c"],SyncMode.LeftToRight);
            using var cancellation = new CancellationTokenSource();
            var result = await executor.ExecuteAsync(plan, plan.Items.Select(i => new SyncDecision(i.RelativePath,i.SuggestedAction)).ToArray(),
                new CancelAfterFirst(cancellation), cancellation.Token);
            Check(result.Cancelled && result.Entries[0].Outcome == SyncOutcome.Copied && result.Entries.Skip(1).All(i => i.Outcome == SyncOutcome.Cancelled), "Batch cancellation result");
            Check(File.Exists(Path.Combine(b,"a")) && !File.Exists(Path.Combine(b,"b")) && !File.Exists(Path.Combine(b,"c")), "Cancellation changed unprocessed files");
        });
    }
    private static void Check(bool value,string message) { if(!value) throw new Exception(message); }
    private static async Task Expect<T>(Func<Task> action) where T:Exception { try { await action(); } catch(T) { return; } throw new Exception("Expected "+typeof(T).Name); }
    private sealed class CancelAfterFirst(CancellationTokenSource source) : IProgress<SyncProgress>
    { public void Report(SyncProgress value) { if (value.Completed == 1) source.Cancel(); } }
}
