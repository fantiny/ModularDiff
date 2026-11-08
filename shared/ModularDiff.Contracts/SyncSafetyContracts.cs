namespace ModularDiff.Contracts;

public enum SyncRecordState { Pending, Skipped, Prepared, Applied, Failed, Cancelled, Restoring, Restored, Conflict }
public sealed record SyncHistoryEntry(string Id, string RelativePath, SyncAction Action, SyncStamp Before, SyncStamp After,
    SyncRecordState State, string? BackupId = null, string? RecoveryBackupId = null, string? Message = null);
public sealed record SyncOperation(string Id, DateTime CreatedUtc, string LeftRoot, string RightRoot,
    string State, IReadOnlyList<SyncHistoryEntry> Entries, string BackupPluginId = "", string JournalPluginId = "");
public sealed record SyncHistoryListing(IReadOnlyList<SyncOperation> Operations, IReadOnlyList<string> Errors);
public sealed record SyncSafetyContext(string StoreRoot, ISyncBackup Backup, ISyncJournal Journal);
public sealed record RecoveryItem(string EntryId, string RelativePath, string Target, string Action, bool CanRestore, string Reason);
public sealed record RecoveryPlan(string OperationId, IReadOnlyList<RecoveryItem> Items);
public sealed record RecoveryResult(string OperationId, int Restored, int Conflicts, int Failed, bool Cancelled);

public interface ISyncBackup : IPlugin
{
    Task<string> CaptureAsync(string storeRoot, string file, SyncStamp expected, CancellationToken cancellationToken = default);
    // The returned stream must stay open and immutable until disposed by the caller.
    Task<Stream> OpenVerifiedAsync(string storeRoot, string backupId, SyncStamp expected, CancellationToken cancellationToken = default);
}
public interface ISyncJournal : IPlugin
{
    Task<IAsyncDisposable> AcquireAsync(string storeRoot, CancellationToken cancellationToken = default);
    Task SaveAsync(string storeRoot, SyncOperation operation, CancellationToken cancellationToken = default);
    Task<SyncOperation> LoadAsync(string storeRoot, string operationId, CancellationToken cancellationToken = default);
    Task<SyncHistoryListing> ListAsync(string storeRoot, CancellationToken cancellationToken = default);
}
public interface ISyncRecovery : IPlugin
{
    Task<RecoveryPlan> PreviewAsync(SyncSafetyContext context, string operationId, CancellationToken cancellationToken = default);
    Task<RecoveryResult> RestoreAsync(SyncSafetyContext context, string operationId, IReadOnlyList<string> entryIds,
        IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default);
}
