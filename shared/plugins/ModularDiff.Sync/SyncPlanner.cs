using ModularDiff.Contracts;

namespace ModularDiff.Sync;

public sealed class SyncPlanner : ISyncPlanner
{
    public string Id => "sync-plan";
    public string DisplayName => "SHA-256 sync planner";
    public async Task<SyncPlan> PlanAsync(string leftRoot, string rightRoot, IReadOnlyList<string> relativePaths, SyncMode mode,
        IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var (left, right) = SyncFiles.Roots(leftRoot, rightRoot);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var paths = relativePaths.Distinct(SyncFiles.Comparer).Order(SyncFiles.Comparer).ToArray();
        var items = new List<SyncItem>();
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var a = await SyncFiles.Stamp(SyncFiles.Resolve(left, path), cancellationToken);
                var b = await SyncFiles.Stamp(SyncFiles.Resolve(right, path), cancellationToken);
                bool conflict = mode == SyncMode.Bidirectional && a.Exists && b.Exists && !SyncFiles.SameContent(a, b);
                var action = SyncFiles.SameContent(a, b) ? SyncAction.Skip : mode switch
                {
                    SyncMode.LeftToRight when a.Exists => SyncAction.LeftToRight,
                    SyncMode.RightToLeft when b.Exists => SyncAction.RightToLeft,
                    SyncMode.Bidirectional when a.Exists && !b.Exists => SyncAction.LeftToRight,
                    SyncMode.Bidirectional when b.Exists && !a.Exists => SyncAction.RightToLeft,
                    _ => SyncAction.Skip
                };
                items.Add(new(path, a, b, action, conflict));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { items.Add(new(path, new(false), new(false), SyncAction.Skip, Problem: ex.Message)); }
            progress?.Report(new(items.Count, paths.Length, path));
        }
        return new(left, right, items.AsReadOnly(), DateTime.UtcNow);
    }
}

public sealed class SyncPreview : ISyncPreview
{
    public string Id => "sync-preview";
    public string DisplayName => "Actionable sync preview";
    public IReadOnlyList<SyncPreviewItem> Describe(SyncPlan plan) => plan.Items.Select(item =>
    {
        var choices = new List<SyncActionChoice> { new(SyncAction.Skip, "跳过") };
        if (item.Problem is null && !SyncFiles.SameContent(item.Left, item.Right))
        {
            if (item.Left.Exists) choices.Add(new(SyncAction.LeftToRight, item.Right.Exists ? "→ 覆盖右侧" : "→ 新增右侧"));
            if (item.Right.Exists) choices.Add(new(SyncAction.RightToLeft, item.Left.Exists ? "← 覆盖左侧" : "← 新增左侧"));
        }
        string Info(SyncStamp stamp) => stamp.Exists ? $"{stamp.Length:N0} B · {new DateTime(stamp.ModifiedUtcTicks, DateTimeKind.Utc).ToLocalTime():yyyy-MM-dd HH:mm:ss}" : "不存在";
        string state = item.Problem ?? (item.Conflict ? "冲突：请选择方向或跳过" : SyncFiles.SameContent(item.Left, item.Right) ? "字节相同" : item.Left.Exists && item.Right.Exists ? "内容不同" : item.Left.Exists ? "仅左侧" : item.Right.Exists ? "仅右侧" : "两侧均不存在");
        return new SyncPreviewItem(item.RelativePath, state, Info(item.Left), Info(item.Right), choices, item.SuggestedAction);
    }).ToArray();
}
