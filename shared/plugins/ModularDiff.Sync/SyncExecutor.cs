using System.Security.Cryptography;
using ModularDiff.Contracts;

namespace ModularDiff.Sync;

public sealed class SyncExecutor : ISyncExecutor
{
    public string Id => "sync-execute";
    public string DisplayName => "Checked atomic file copy";
    public Task<SyncResult> ExecuteAsync(SyncPlan plan, IReadOnlyList<SyncDecision> decisions,
        IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default)
        => ExecuteAsync(plan, decisions, SafetyStore.Default(), progress, cancellationToken);
    public async Task<SyncResult> ExecuteAsync(SyncPlan plan, IReadOnlyList<SyncDecision> decisions, SyncSafetyContext safety,
        IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        SyncFiles.Roots(plan.LeftRoot, plan.RightRoot);
        ArgumentNullException.ThrowIfNull(safety); SafetyStore.Outside(safety.StoreRoot, plan.LeftRoot, plan.RightRoot);
        var items = plan.Items.ToDictionary(i => i.RelativePath, SyncFiles.Comparer);
        var selected = new Dictionary<string, SyncAction>(SyncFiles.Comparer);
        foreach (var decision in decisions)
            if (!items.ContainsKey(decision.RelativePath) || !Enum.IsDefined(decision.Action) || !selected.TryAdd(decision.RelativePath, decision.Action))
                throw new ArgumentException("同步决策包含未知路径、重复路径或无效操作。");
        // A single vault lease serializes sync and recovery across processes.
        await using var lease = await safety.Journal.AcquireAsync(safety.StoreRoot, CancellationToken.None);
        var entries = plan.Items.Select(item =>
        {
            var action = selected.GetValueOrDefault(item.RelativePath, SyncAction.Skip);
            return new SyncHistoryEntry(Guid.NewGuid().ToString("N"), item.RelativePath, action,
                action == SyncAction.LeftToRight ? item.Right : item.Left, action == SyncAction.LeftToRight ? item.Left : item.Right,
                action == SyncAction.Skip ? SyncRecordState.Skipped : SyncRecordState.Pending);
        }).ToArray();
        var operation = new SyncOperation(Guid.NewGuid().ToString("N"), DateTime.UtcNow, plan.LeftRoot, plan.RightRoot, "Running", entries, safety.Backup.Id, safety.Journal.Id);
        async Task Save()
        {
            try { await safety.Journal.SaveAsync(safety.StoreRoot, operation with { Entries = entries.ToArray() }, CancellationToken.None); }
            catch (Exception ex) { throw new JournalWriteException("操作记录写入失败，已停止后续文件：" + ex.Message, ex); }
        }
        await Save(); // Fail closed before any target writes.
        var results = new List<SyncEntryResult>(); bool cancelled = false, stopped = false;
        for (int index = 0; index < plan.Items.Count; index++)
        {
            var item = plan.Items[index]; var action = entries[index].Action;
            SyncOutcome outcome = SyncOutcome.Skipped; string message = "未选择复制"; bool copied = false;
            if (action != SyncAction.Skip)
            {
                if (stopped) { outcome = SyncOutcome.Failed; message = "记录库异常，本文件未执行"; }
                else
                {
                    try
                    {
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (item.Problem is not null) throw new IOException(item.Problem);
                            await Copy(plan, item, action, async target =>
                            {
                                string? backup = entries[index].Before.Exists
                                    ? await safety.Backup.CaptureAsync(safety.StoreRoot, target, entries[index].Before, cancellationToken) : null;
                                entries[index] = entries[index] with { State = SyncRecordState.Prepared, BackupId = backup };
                                await Save(); // Prepared record contains the verified backup before replacement.
                            }, cancellationToken);
                            copied = true; outcome = SyncOutcome.Copied; message = "已复制（可从同步历史恢复）";
                            entries[index] = entries[index] with { State = SyncRecordState.Applied, Message = message };
                        }
                        catch (JournalWriteException) { throw; }
                        catch (OperationCanceledException)
                        {
                            cancelled = true; outcome = SyncOutcome.Cancelled; message = "已取消；先前完成的文件保留";
                            entries[index] = entries[index] with { State = entries[index].State == SyncRecordState.Prepared ? SyncRecordState.Prepared : SyncRecordState.Cancelled, Message = message };
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                        {
                            outcome = SyncOutcome.Failed; message = ex.Message;
                            entries[index] = entries[index] with { State = entries[index].State == SyncRecordState.Prepared ? SyncRecordState.Prepared : SyncRecordState.Failed, Message = message };
                        }
                        await Save();
                    }
                    catch (JournalWriteException ex) { stopped = true; outcome = copied ? SyncOutcome.Copied : SyncOutcome.Failed; message = ex.Message + "；请在历史中检查准备记录。"; }
                }
            }
            results.Add(new(item.RelativePath, action, outcome, message)); progress?.Report(new(results.Count, plan.Items.Count, item.RelativePath));
        }
        cancelled |= cancellationToken.IsCancellationRequested;
        if (!stopped)
        {
            operation = operation with { State = cancelled ? "Cancelled" : results.Any(r => r.Outcome == SyncOutcome.Failed) ? "Partial" : "Completed" };
            try { await Save(); } catch (JournalWriteException ex) { results.Add(new("[记录]", SyncAction.Skip, SyncOutcome.Failed, ex.Message)); }
        }
        return new(results.AsReadOnly(), cancelled, operation.Id);
    }
    private sealed class JournalWriteException(string message, Exception inner) : IOException(message, inner);
    private static async Task Copy(SyncPlan plan, SyncItem item, SyncAction action, Func<string, Task> prepare, CancellationToken token)
    {
        await SyncFiles.Validate(plan, item, token);
        bool right = action == SyncAction.LeftToRight;
        var sourceStamp = right ? item.Left : item.Right; var targetStamp = right ? item.Right : item.Left;
        if (!sourceStamp.Exists) throw new IOException("复制来源不存在。");
        string source = SyncFiles.Resolve(right ? plan.LeftRoot : plan.RightRoot, item.RelativePath);
        string target = SyncFiles.Resolve(right ? plan.RightRoot : plan.LeftRoot, item.RelativePath);
        if (targetStamp.Exists && (File.GetAttributes(target) & FileAttributes.ReadOnly) != 0) throw new UnauthorizedAccessException("目标文件为只读。");
        string directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory); SyncFiles.NoLinks(directory);
        string temporary = Path.Combine(directory, ".modulardiff-sync-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, token); await output.FlushAsync(token); output.Flush(true);
            }
            var copied = await SyncFiles.Stamp(temporary, token);
            if (!SyncFiles.SameContent(copied, sourceStamp)) throw new IOException("复制期间源内容变化。");
            File.SetLastWriteTimeUtc(temporary, new(sourceStamp.ModifiedUtcTicks, DateTimeKind.Utc));
            await prepare(target);
            await SyncFiles.Validate(plan, item, token); token.ThrowIfCancellationRequested();
            if (targetStamp.Exists) File.Replace(temporary, target, null); else File.Move(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
