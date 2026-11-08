namespace ModularDiff.Contracts;

public enum SyncMode { LeftToRight, RightToLeft, Bidirectional }
public enum SyncAction { Skip, LeftToRight, RightToLeft }
public sealed record SyncStamp(bool Exists, long Length = 0, string Hash = "", long ModifiedUtcTicks = 0);
public sealed record SyncItem(string RelativePath, SyncStamp Left, SyncStamp Right, SyncAction SuggestedAction,
    bool Conflict = false, string? Problem = null);
public sealed record SyncPlan(string LeftRoot, string RightRoot, IReadOnlyList<SyncItem> Items, DateTime CreatedUtc);
public sealed record SyncDecision(string RelativePath, SyncAction Action);
public sealed record SyncProgress(int Completed, int Total, string RelativePath);
public enum SyncOutcome { Copied, Skipped, Failed, Cancelled }
public sealed record SyncEntryResult(string RelativePath, SyncAction Action, SyncOutcome Outcome, string Message);
public sealed record SyncResult(IReadOnlyList<SyncEntryResult> Entries, bool Cancelled, string? OperationId)
{
    public SyncResult(IReadOnlyList<SyncEntryResult> entries, bool cancelled) : this(entries, cancelled, null) { }
}
public sealed record SyncActionChoice(SyncAction Action, string Label);
public sealed record SyncPreviewItem(string RelativePath, string State, string LeftInfo, string RightInfo,
    IReadOnlyList<SyncActionChoice> Choices, SyncAction SuggestedAction);

public interface ISyncPlanner : IPlugin
{
    // Only explicitly supplied relative paths are planned; no writes occur during planning.
    Task<SyncPlan> PlanAsync(string leftRoot, string rightRoot, IReadOnlyList<string> relativePaths, SyncMode mode,
        IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default);
}
public interface ISyncPreview : IPlugin { IReadOnlyList<SyncPreviewItem> Describe(SyncPlan plan); }
public interface ISyncExecutor : IPlugin
{
    Task<SyncResult> ExecuteAsync(SyncPlan plan, IReadOnlyList<SyncDecision> decisions,
        IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default);
    // Existing executors remain loadable, but cannot silently bypass protection in the GUI.
    Task<SyncResult> ExecuteAsync(SyncPlan plan, IReadOnlyList<SyncDecision> decisions, SyncSafetyContext safety,
        IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("此执行插件尚未支持自动备份和操作记录。");
}
