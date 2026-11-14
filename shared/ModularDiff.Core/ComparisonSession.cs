using ModularDiff.Contracts;

namespace ModularDiff.Core;

/// <summary>UI-neutral comparison session: owns the edit buffers, the latest buffer comparison,
/// the aligned presentation, and the active options. All document operations go through the
/// existing plugin contracts; both GUIs (WPF and Avalonia) drive this single class instead of
/// keeping parallel scattered state fields. A Version counter lets async UI operations detect
/// when their snapshot has been superseded.</summary>
public sealed class ComparisonSession(PluginCatalog plugins)
{
    public IEditBuffer? Left { get; private set; }
    public IEditBuffer? Right { get; private set; }
    public BufferComparison? Comparison { get; private set; }
    public TextPresentation? Presentation { get; private set; }
    public string Algorithm { get; set; } = "myers";
    public ComparisonOptions Options { get; set; } = new();
    public bool Dirty => Left?.IsDirty == true || Right?.IsDirty == true;
    /// <summary>Increments on every mutation (Load/Clear/Recompare/Edit/Merge); async UI
    /// operations compare their captured version to detect staleness.</summary>
    public long Version { get; private set; }

    private T Plugin<T>() where T : class, IPlugin =>
        plugins.GetAll<T>().FirstOrDefault() ?? throw new InvalidOperationException($"缺少插件 {typeof(T).Name}");

    public async Task LoadAsync(string left, string right, CancellationToken cancellationToken = default)
    {
        var storage = Plugin<ITextStorage>();
        var snapshots = await Task.WhenAll(storage.LoadAsync(left, true, cancellationToken), storage.LoadAsync(right, true, cancellationToken));
        var factory = Plugin<IEditBufferFactory>();
        Left = factory.Create(snapshots[0]);
        Right = factory.Create(snapshots[1]);
        Recompare();
    }

    public void Clear()
    {
        Left = Right = null;
        Comparison = null;
        Presentation = null;
        Version++;
    }

    /// <summary>Attaches pre-created buffers (used by WPF's AttachEditors pipeline).</summary>
    public void Attach(IEditBuffer left, IEditBuffer right)
    {
        Left = left;
        Right = right;
        Version++;
    }

    /// <summary>Stores a computed comparison result (used by WPF's async recompare pipeline).</summary>
    public void SetComparison(BufferComparison? comparison)
    {
        Comparison = comparison;
        Version++;
    }

    /// <summary>Stores a computed presentation (used by WPF's async rebuild pipeline).</summary>
    public void SetPresentation(TextPresentation? presentation)
    {
        Presentation = presentation;
        Version++;
    }

    public void Recompare()
    {
        if (Left is null || Right is null) return;
        Comparison = null;
        Presentation = null;
        Version++;
        Comparison = Plugin<IBufferComparison>().Compare(Left.Snapshot(), Right.Snapshot(), Algorithm, Options, plugins);
        Presentation = Plugin<ITextPresentation>().Build(Comparison.File.Lines);
    }

    /// <summary>Replaces one side's entire text with minimal-span edit detection.</summary>
    public void Edit(bool left, string text)
    {
        var buffer = (left ? Left : Right) ?? throw new InvalidOperationException("请先打开文本文件。");
        string old = buffer.Text;
        if (old == text) return;
        int start = 0, end = 0;
        while (start < old.Length && start < text.Length && old[start] == text[start]) start++;
        while (end < old.Length - start && end < text.Length - start && old[^(end + 1)] == text[^(end + 1)]) end++;
        buffer.Apply(new(start, old.Length - start - end, text.Substring(start, text.Length - start - end)));
        Comparison = null;
        Presentation = null;
        Version++;
    }

    public void Merge(int hunk, MergeDirection direction)
    {
        Recompare();
        if (Comparison is null || Left is null || Right is null) return;
        var edit = Plugin<IBlockMerge>().Plan(Comparison, Left.Snapshot(), Right.Snapshot(), hunk, direction);
        (direction == MergeDirection.LeftToRight ? Right : Left).Apply(edit);
        Recompare();
    }

    /// <summary>一次复制全部差异块(BC 的 Copy All):同一份快照规划所有编辑,自底向上应用,
    /// 低处偏移永不被高处编辑推移,最后只重比较一次。返回复制的块数。</summary>
    public int MergeAll(MergeDirection direction)
    {
        Recompare();
        if (Comparison is null || Left is null || Right is null || Presentation?.Hunks.Count is not > 0) return 0;
        var leftSnapshot = Left.Snapshot();
        var rightSnapshot = Right.Snapshot();
        var merge = Plugin<IBlockMerge>();
        var edits = new List<TextEdit>();
        for (int index = Presentation.Hunks.Count - 1; index >= 0; index--)
            edits.Add(merge.Plan(Comparison, leftSnapshot, rightSnapshot, Presentation.Hunks[index].Id, direction));
        var destination = direction == MergeDirection.LeftToRight ? Right : Left;
        foreach (var edit in edits) destination!.Apply(edit);
        Recompare();
        return edits.Count;
    }

    public void History(bool left, bool redo)
    {
        var buffer = left ? Left : Right;
        if (buffer is null) return;
        if (redo) buffer.Redo(); else buffer.Undo();
        Recompare();
    }

    public async Task SaveAsync(bool left)
    {
        var buffer = left ? Left : Right;
        if (buffer is null || !buffer.IsDirty) return;
        var snapshot = buffer.Snapshot();
        var stamp = await Plugin<ITextStorage>().SaveAsync(snapshot);
        buffer.AcceptSave(snapshot, stamp);
    }
}
