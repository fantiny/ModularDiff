using ModularDiff.Contracts;

namespace ModularDiff.Sync;

public sealed class SyncRecovery : ISyncRecovery
{
    public string Id => "sync-recovery";
    public string DisplayName => "Conflict-checked sync recovery";
    private static bool Eligible(SyncHistoryEntry entry) => entry.State is SyncRecordState.Applied or SyncRecordState.Prepared or SyncRecordState.Restoring or SyncRecordState.Conflict;
    private static string Target(SyncOperation operation, SyncHistoryEntry entry)
    {
        if (entry.Action is not (SyncAction.LeftToRight or SyncAction.RightToLeft)) throw new IOException("记录中复制方向无效。");
        return SyncFiles.Resolve(entry.Action == SyncAction.LeftToRight ? operation.RightRoot : operation.LeftRoot, entry.RelativePath);
    }
    private static void Validate(SyncSafetyContext context, SyncOperation operation)
    {
        if (!string.IsNullOrEmpty(operation.BackupPluginId) && operation.BackupPluginId != context.Backup.Id) throw new IOException("请在设置中选择备份插件：" + operation.BackupPluginId);
        if (!string.IsNullOrEmpty(operation.JournalPluginId) && operation.JournalPluginId != context.Journal.Id) throw new IOException("请在设置中选择记录插件：" + operation.JournalPluginId);
        SafetyStore.Outside(context.StoreRoot, operation.LeftRoot, operation.RightRoot);
        SyncFiles.Roots(operation.LeftRoot, operation.RightRoot);
    }
    public async Task<RecoveryPlan> PreviewAsync(SyncSafetyContext context, string operationId, CancellationToken cancellationToken = default)
    {
        var operation = await context.Journal.LoadAsync(context.StoreRoot, operationId, cancellationToken); Validate(context, operation);
        var rows = new List<RecoveryItem>();
        foreach (var entry in operation.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested(); string target = ""; bool can = false; string reason;
            try
            {
                if (entry.Action == SyncAction.Skip) reason = "原操作跳过";
                else
                {
                    target = Target(operation, entry);
                    var current = await SyncFiles.Stamp(target, cancellationToken);
                    can = Eligible(entry) && (current == entry.After || current == entry.Before);
                    reason = !Eligible(entry) ? entry.State == SyncRecordState.Restored ? "已经恢复" : entry.Message ?? "该项未执行"
                        : current == entry.Before ? "目标已是原始状态；只更新记录"
                        : can ? "可恢复；执行前再次校验" : "冲突：目标在同步后发生变化，保留当前文件";
                    if (can && entry.Before.Exists)
                    { await using var backup = await context.Backup.OpenVerifiedAsync(context.StoreRoot, entry.BackupId ?? throw new IOException("缺少原始备份。"), entry.Before, cancellationToken); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { reason = ex.Message; can = false; }
            rows.Add(new(entry.Id, entry.RelativePath, target, entry.Before.Exists ? "恢复覆盖前版本" : "移除同步新增文件（先留存副本）", can, reason));
        }
        return new(operationId, rows);
    }
    public async Task<RecoveryResult> RestoreAsync(SyncSafetyContext context, string operationId, IReadOnlyList<string> entryIds,
        IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await using var lease = await context.Journal.AcquireAsync(context.StoreRoot, cancellationToken);
        var operation = await context.Journal.LoadAsync(context.StoreRoot, operationId, cancellationToken); Validate(context, operation);
        var ids = entryIds.ToHashSet(StringComparer.Ordinal);
        if (ids.Count != entryIds.Count || ids.Any(id => !operation.Entries.Any(e => e.Id == id))) throw new ArgumentException("未知或重复的恢复项目。");
        var entries = operation.Entries.ToArray(); int restored = 0, conflicts = 0, failed = 0; bool cancelled = false;
        async Task Save() => await context.Journal.SaveAsync(context.StoreRoot, operation with { Entries = entries.ToArray() }, CancellationToken.None);
        for (int index = entries.Length - 1; index >= 0; index--)
        {
            var entry = entries[index]; if (!ids.Contains(entry.Id) || !Eligible(entry)) continue;
            if (cancellationToken.IsCancellationRequested) { cancelled = true; break; }
            string? temporary = null; bool mutationPrepared = false;
            try
            {
                string target = Target(operation, entry);
                var current = await SyncFiles.Stamp(target, cancellationToken);
                if (current == entry.Before)
                {
                    entries[index] = entry with { State = SyncRecordState.Restored, Message = "目标已经是原始状态" };
                    await Save(); restored++; continue;
                }
                if (current != entry.After)
                {
                    entries[index] = entry with { State = SyncRecordState.Conflict, Message = "目标在同步后变化；未覆盖" };
                    await Save(); conflicts++; continue;
                }
                if ((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0) throw new UnauthorizedAccessException("目标为只读，不能恢复。");
                if (entry.Before.Exists)
                {
                    await using var backup = await context.Backup.OpenVerifiedAsync(context.StoreRoot, entry.BackupId ?? throw new IOException("缺少原始备份。"), entry.Before, cancellationToken);
                    temporary = Path.Combine(Path.GetDirectoryName(target)!, ".modulardiff-restore-" + Guid.NewGuid().ToString("N") + ".tmp");
                    await SafetyStore.CopyFlushed(backup, temporary, cancellationToken);
                    if (!SyncFiles.SameContent(await SyncFiles.Stamp(temporary, cancellationToken), entry.Before)) throw new IOException("恢复临时文件校验失败。");
                    File.SetLastWriteTimeUtc(temporary, new(entry.Before.ModifiedUtcTicks, DateTimeKind.Utc));
                }
                string recoveryBackup = await context.Backup.CaptureAsync(context.StoreRoot, target, current, cancellationToken);
                entries[index] = entry with { State = SyncRecordState.Restoring, RecoveryBackupId = recoveryBackup, Message = "恢复准备完成" };
                await Save(); mutationPrepared = true;
                // Resolve containment and links immediately before any replacement/deletion.
                target = Target(operation, entry);
                if (await SyncFiles.Stamp(target, cancellationToken) != current) throw new IOException("恢复期间目标又发生变化。");
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Before.Exists) File.Replace(temporary!, target, null);
                else File.Delete(target); // Only a recorded newly-created file matching its post-sync fingerprint.
                entries[index] = entries[index] with { State = SyncRecordState.Restored, Message = "已恢复；恢复前副本已留存" };
                await Save(); restored++;
            }
            catch (OperationCanceledException) { cancelled = true; break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                failed++;
                // Keep Restoring if the replacement may have happened; retry reconciles against Before/After.
                entries[index] = entries[index] with { State = mutationPrepared ? SyncRecordState.Restoring : entry.State, Message = "恢复未完成：" + ex.Message };
                await Save(); // A journal failure aborts the rest of the operation, never continues unrecorded.
            }
            finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
            progress?.Report(new(restored + conflicts + failed, ids.Count, entry.RelativePath));
        }
        operation = operation with { State = entries.Any(Eligible) ? "RecoveryPartial" : entries.Any(e => e.State == SyncRecordState.Restored) ? "Restored" : operation.State };
        await Save(); return new(operationId, restored, conflicts, failed, cancelled);
    }
}
