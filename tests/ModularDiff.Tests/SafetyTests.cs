using ModularDiff.Contracts;
using ModularDiff.Core;

internal static class SafetyTests
{
    public static async Task Run(PluginCatalog plugins, string temp, Func<string, Func<Task>, Task> test)
    {
        var planner = plugins.Get<ISyncPlanner>("sync-plan"); var executor = plugins.Get<ISyncExecutor>("sync-execute");
        var backup = plugins.Get<ISyncBackup>("sync-backup"); var journal = plugins.Get<ISyncJournal>("sync-journal"); var recovery = plugins.Get<ISyncRecovery>("sync-recovery");
        async Task<(string Left, string Right, SyncSafetyContext Context, SyncPlan Plan)> Setup()
        {
            string root = Path.Combine(temp, "safety-" + Guid.NewGuid().ToString("N")), left = Path.Combine(root,"left"), right = Path.Combine(root,"right");
            Directory.CreateDirectory(left); Directory.CreateDirectory(right);
            await File.WriteAllTextAsync(Path.Combine(left,"file"), "new"); await File.WriteAllTextAsync(Path.Combine(right,"file"), "original");
            return (left,right,new(Path.Combine(root,"history"),backup,journal),await planner.PlanAsync(left,right,["file"],SyncMode.LeftToRight));
        }
        Task<SyncResult> Execute(SyncPlan plan, SyncSafetyContext context) => executor.ExecuteAsync(plan, plan.Items.Select(i=>new SyncDecision(i.RelativePath,i.SuggestedAction)).ToArray(), safety:context);
        async Task<RecoveryResult> Restore(SyncSafetyContext context,string id)
        {
            var record=await journal.LoadAsync(context.StoreRoot,id);
            return await recovery.RestoreAsync(context,id,record.Entries.Select(e=>e.Id).ToArray());
        }
        await test("Safety: persistent backups restore overwritten bytes/time and remove only recorded new files",async()=>
        {
            var (a,b,c,_) = await Setup(); var beforeTime=File.GetLastWriteTimeUtc(Path.Combine(b,"file"));
            await File.WriteAllBytesAsync(Path.Combine(a,"binary"),[0,255,1,128]);
            var plan=await planner.PlanAsync(a,b,["file","binary"],SyncMode.LeftToRight);
            var result=await Execute(plan,c); var record=await journal.LoadAsync(c.StoreRoot,result.OperationId!);
            Check(record.Entries.All(e=>e.State==SyncRecordState.Applied),"Applied journal missing");
            Check(record.Entries.Single(e=>e.RelativePath=="file").BackupId is not null,"Overwrite has no backup");
            var restored=await Restore(c,record.Id); Check(restored.Restored==2 && restored.Failed==0,"Restore failed");
            Check(await File.ReadAllTextAsync(Path.Combine(b,"file"))=="original" && File.GetLastWriteTimeUtc(Path.Combine(b,"file"))==beforeTime,"Original bytes/time lost");
            Check(!File.Exists(Path.Combine(b,"binary")) && File.Exists(Path.Combine(a,"binary")),"New file recovery touched source");
            record=await journal.LoadAsync(c.StoreRoot,record.Id); Check(record.Entries.All(e=>e.RecoveryBackupId is not null && e.State==SyncRecordState.Restored),"Recovery copies/history missing");
            Check((await Restore(c,record.Id)).Restored==0,"Repeated recovery was not idempotent");
        });
        await test("Safety: newer user changes conflict without overwriting or deleting them",async()=>
        {
            var (a,b,c,p)=await Setup(); var result=await Execute(p,c);
            await File.WriteAllTextAsync(Path.Combine(b,"file"),"user edit after sync");
            Check(!(await recovery.PreviewAsync(c,result.OperationId!)).Items.Single().CanRestore,"Conflict preview enabled restore");
            var restored=await Restore(c,result.OperationId!); Check(restored.Conflicts==1 && await File.ReadAllTextAsync(Path.Combine(b,"file"))=="user edit after sync","Conflict overwritten");
        });
        await test("Safety: backup failure and journal failure before commit leave target untouched",async()=>
        {
            var (a,b,c,p)=await Setup(); var badBackup=c with {Backup=new FailingBackup(backup)};
            Check((await Execute(p,badBackup)).Entries.Single().Outcome==SyncOutcome.Failed,"Backup failure ignored");
            Check(await File.ReadAllTextAsync(Path.Combine(b,"file"))=="original","Wrote despite backup failure");
            await Expect<IOException>(()=>Execute(p,c with {Journal=new FailingJournal(journal,1)}));
            Check(await File.ReadAllTextAsync(Path.Combine(b,"file"))=="original","Wrote despite initial journal failure");
            var result=await Execute(p,c with {Journal=new FailingJournal(journal,2)});
            Check(result.Entries.Single().Outcome==SyncOutcome.Failed && await File.ReadAllTextAsync(Path.Combine(b,"file"))=="original","Wrote despite prepare journal failure");
        });
        await test("Safety: post-commit journal failure leaves durable Prepared record recoverable",async()=>
        {
            var (a,b,c,p)=await Setup(); var result=await Execute(p,c with {Journal=new FailingJournal(journal,3)});
            Check(await File.ReadAllTextAsync(Path.Combine(b,"file"))=="new","Commit not reached");
            var record=await journal.LoadAsync(c.StoreRoot,result.OperationId!); Check(record.Entries.Single().State==SyncRecordState.Prepared,"Prepared record lost");
            Check((await Restore(c,record.Id)).Restored==1 && await File.ReadAllTextAsync(Path.Combine(b,"file"))=="original","Prepared recovery failed");
        });
        await test("Safety: damaged/missing backup never replaces target; corrupted records are isolated",async()=>
        {
            var (a,b,c,p)=await Setup(); var result=await Execute(p,c); var record=await journal.LoadAsync(c.StoreRoot,result.OperationId!);
            string asset=Path.Combine(c.StoreRoot,"backups",record.Entries.Single().BackupId+".bin"); await File.WriteAllTextAsync(asset,"corrupted");
            Check(!(await recovery.PreviewAsync(c,record.Id)).Items.Single().CanRestore,"Corrupted backup accepted");
            Check((await Restore(c,record.Id)).Failed==1 && await File.ReadAllTextAsync(Path.Combine(b,"file"))=="new","Bad backup overwrote target");
            File.Delete(asset); Check((await Restore(c,record.Id)).Failed==1,"Missing backup accepted");
            await File.WriteAllTextAsync(Path.Combine(c.StoreRoot,"operations",Guid.NewGuid().ToString("N")+".json"),"{broken");
            var listing=await journal.ListAsync(c.StoreRoot); Check(listing.Operations.Count==1 && listing.Errors.Count==1,"Corrupted record hid valid records");
        });
        await test("Safety: operations must unwind newest first and recovery can resume after journal failure",async()=>
        {
            var (a,b,c,p)=await Setup(); var first=await Execute(p,c);
            await File.WriteAllTextAsync(Path.Combine(a,"file"),"second sync"); p=await planner.PlanAsync(a,b,["file"],SyncMode.LeftToRight); var second=await Execute(p,c);
            Check((await Restore(c,first.OperationId!)).Conflicts==1,"Older operation overwrote newer sync");
            var record=await journal.LoadAsync(c.StoreRoot,second.OperationId!);
            await recovery.RestoreAsync(c with {Journal=new FailingJournal(journal,2)},record.Id,[record.Entries.Single().Id]);
            Check(await File.ReadAllTextAsync(Path.Combine(b,"file"))=="new","Recovery commit not reached");
            await Restore(c,record.Id); Check((await Restore(c,first.OperationId!)).Restored==1,"Chained recovery failed");
            Check(await File.ReadAllTextAsync(Path.Combine(b,"file"))=="original","Original version lost");
        });
        await test("Safety: vault lease, storage containment and backup identifiers are enforced",async()=>
        {
            var (a,b,c,p)=await Setup();
            await using(var lease=await journal.AcquireAsync(c.StoreRoot)) await Expect<IOException>(()=>journal.AcquireAsync(c.StoreRoot));
            await Expect<IOException>(()=>Execute(p,c with {StoreRoot=Path.Combine(b,"history")}));
            await Expect<IOException>(()=>backup.OpenVerifiedAsync(c.StoreRoot,"../escape",new(true,1,"x")));
            Check(await File.ReadAllTextAsync(Path.Combine(b,"file"))=="original","Invalid safety configuration changed target");
        });
        await test("Safety: newly-created files with later edits are never deleted by recovery",async()=>
        {
            var (a,b,c,_) = await Setup(); await File.WriteAllTextAsync(Path.Combine(a,"added"),"created by sync");
            var p=await planner.PlanAsync(a,b,["added"],SyncMode.LeftToRight); var result=await Execute(p,c);
            await File.WriteAllTextAsync(Path.Combine(b,"added"),"later user content");
            Check((await Restore(c,result.OperationId!)).Conflicts==1 && await File.ReadAllTextAsync(Path.Combine(b,"added"))=="later user content","Recovery deleted later user data");
        });
        await test("Safety: partial recovery cancellation persists progress and resumes remaining items",async()=>
        {
            var (a,b,c,_) = await Setup();
            foreach(string name in new[]{"a","b","c"}) await File.WriteAllTextAsync(Path.Combine(a,name),name);
            var p=await planner.PlanAsync(a,b,["a","b","c"],SyncMode.LeftToRight); var result=await Execute(p,c);
            var record=await journal.LoadAsync(c.StoreRoot,result.OperationId!); using var cancel=new CancellationTokenSource();
            var restored=await recovery.RestoreAsync(c,record.Id,record.Entries.Select(e=>e.Id).ToArray(),new CancelAfterOne(cancel),cancel.Token);
            Check(restored.Cancelled && restored.Restored==1,"Recovery did not stop after completed item");
            Check((await journal.LoadAsync(c.StoreRoot,record.Id)).Entries.Count(e=>e.State==SyncRecordState.Restored)==1,"Partial state not durable");
            Check((await Restore(c,record.Id)).Restored==2,"Recovery did not resume remaining items");
        });
    }
    private static void Check(bool condition,string message) { if(!condition) throw new Exception(message); }
    private static async Task Expect<T>(Func<Task> action) where T:Exception { try { await action(); } catch(T) { return; } throw new Exception("Expected "+typeof(T).Name); }
    private sealed class CancelAfterOne(CancellationTokenSource source):IProgress<SyncProgress>
    { public void Report(SyncProgress value) { if(value.Completed==1) source.Cancel(); } }
    private sealed class FailingBackup(ISyncBackup inner):ISyncBackup
    {
        public string Id=>inner.Id; public string DisplayName=>inner.DisplayName;
        public Task<string> CaptureAsync(string root,string path,SyncStamp stamp,CancellationToken token=default)=>throw new IOException("Injected backup failure");
        public Task<Stream> OpenVerifiedAsync(string root,string id,SyncStamp stamp,CancellationToken token=default)=>inner.OpenVerifiedAsync(root,id,stamp,token);
    }
    private sealed class FailingJournal(ISyncJournal inner,int failAt):ISyncJournal
    {
        private int _writes;
        public string Id=>inner.Id; public string DisplayName=>inner.DisplayName;
        public Task<IAsyncDisposable> AcquireAsync(string root,CancellationToken token=default)=>inner.AcquireAsync(root,token);
        public Task SaveAsync(string root,SyncOperation operation,CancellationToken token=default)=>++_writes==failAt ? throw new IOException("Injected journal failure") : inner.SaveAsync(root,operation,token);
        public Task<SyncOperation> LoadAsync(string root,string id,CancellationToken token=default)=>inner.LoadAsync(root,id,token);
        public Task<SyncHistoryListing> ListAsync(string root,CancellationToken token=default)=>inner.ListAsync(root,token);
    }
}

internal sealed class TestSyncExecutor(ISyncExecutor inner, SyncSafetyContext context):ISyncExecutor
{
    public string Id=>inner.Id; public string DisplayName=>inner.DisplayName;
    public Task<SyncResult> ExecuteAsync(SyncPlan plan,IReadOnlyList<SyncDecision> decisions,IProgress<SyncProgress>? progress=null,CancellationToken cancellationToken=default)
        =>inner.ExecuteAsync(plan,decisions,context,progress,cancellationToken);
    public Task<SyncResult> ExecuteAsync(SyncPlan plan,IReadOnlyList<SyncDecision> decisions,SyncSafetyContext safety,IProgress<SyncProgress>? progress=null,CancellationToken cancellationToken=default)
        =>inner.ExecuteAsync(plan,decisions,safety,progress,cancellationToken);
}
