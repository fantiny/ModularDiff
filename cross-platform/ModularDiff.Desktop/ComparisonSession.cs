using ModularDiff.Contracts;
using ModularDiff.Core;
namespace ModularDiff.Desktop;
// UI-neutral session: all document operations are supplied by the existing plugins.
public sealed class ComparisonSession(PluginCatalog plugins)
{
    public IEditBuffer? Left { get; private set; }
    public IEditBuffer? Right { get; private set; }
    public BufferComparison? Comparison { get; private set; }
    public TextPresentation? Presentation { get; private set; }
    public string Algorithm { get; set; } = "myers";
    public ComparisonOptions Options { get; set; } = new();
    public bool Dirty => Left?.IsDirty == true || Right?.IsDirty == true;
    private T Plugin<T>() where T : class, IPlugin => plugins.GetAll<T>().FirstOrDefault() ?? throw new InvalidOperationException($"缺少插件 {typeof(T).Name}");
    public async Task LoadAsync(string left, string right)
    {
        var storage = Plugin<ITextStorage>();
        var snapshots = await Task.WhenAll(storage.LoadAsync(left, true), storage.LoadAsync(right, true));
        var factory = Plugin<IEditBufferFactory>();
        var a = factory.Create(snapshots[0]); var b = factory.Create(snapshots[1]);
        Left = a; Right = b; Recompare();
    }
    public void Clear() { Left = Right = null; Comparison = null; Presentation = null; }
    public void Recompare()
    {
        if (Left is null || Right is null) return;
        Comparison = null; Presentation = null;
        Comparison = Plugin<IBufferComparison>().Compare(Left.Snapshot(), Right.Snapshot(), Algorithm, Options, plugins);
        Presentation = Plugin<ITextPresentation>().Build(Comparison.File.Lines);
    }
    public void Edit(bool left, string text)
    {
        var buffer = (left ? Left : Right) ?? throw new InvalidOperationException("请先打开文本文件。");
        // Store only the changed span, preserving useful undo steps for large documents.
        string old = buffer.Text; if (old == text) return;
        int start = 0, end = 0;
        while (start < old.Length && start < text.Length && old[start] == text[start]) start++;
        while (end < old.Length - start && end < text.Length - start && old[^(end + 1)] == text[^(end + 1)]) end++;
        buffer.Apply(new(start, old.Length - start - end, text.Substring(start, text.Length - start - end)));
        Comparison = null; Presentation = null;
    }
    public void Merge(int hunk, MergeDirection direction)
    {
        Recompare();
        if (Comparison is null || Left is null || Right is null) return;
        var edit = Plugin<IBlockMerge>().Plan(Comparison, Left.Snapshot(), Right.Snapshot(), hunk, direction);
        (direction == MergeDirection.LeftToRight ? Right : Left).Apply(edit); Recompare();
    }
    /// <summary>一次复制全部差异块(BC 的 Copy All):同一份快照规划所有编辑,自底向上应用,
    /// 低处偏移永不被高处编辑推移,最后只重比较一次。返回复制的块数。</summary>
    public int MergeAll(MergeDirection direction)
    {
        Recompare();
        if (Comparison is null || Left is null || Right is null || Presentation?.Hunks.Count is not > 0) return 0;
        var leftSnapshot = Left.Snapshot(); var rightSnapshot = Right.Snapshot();
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
        var buffer = left ? Left : Right; if (buffer is null) return;
        if (redo) buffer.Redo(); else buffer.Undo(); Recompare();
    }
    public async Task SaveAsync(bool left)
    {
        var buffer = left ? Left : Right; if (buffer is null || !buffer.IsDirty) return;
        var snapshot = buffer.Snapshot();
        var stamp = await Plugin<ITextStorage>().SaveAsync(snapshot);
        buffer.AcceptSave(snapshot, stamp);
    }
}
