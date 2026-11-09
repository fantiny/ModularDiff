namespace ModularDiff.Contracts;

public enum MergeSectionKind
{
    /// <summary>三方一致（或该段无任何一方修改）：直接输出。</summary>
    Stable,
    /// <summary>仅左侧相对 base 修改：自动采用左侧。</summary>
    AutoLeft,
    /// <summary>仅右侧相对 base 修改：自动采用右侧。</summary>
    AutoRight,
    /// <summary>两侧改成相同内容：自动采用（一份）。</summary>
    BothSame,
    /// <summary>两侧都改且内容不同：冲突，需人工决策。</summary>
    Conflict,
}

/// <summary>合并结果的一个区段。Stable 段 Left==Right==内容；冲突段携带两侧版本。</summary>
public sealed record MergeSection(MergeSectionKind Kind, IReadOnlyList<string> Left, IReadOnlyList<string> Right)
{
    public IReadOnlyList<string> Resolved => Kind switch
    {
        MergeSectionKind.AutoLeft => Left,
        MergeSectionKind.AutoRight or MergeSectionKind.BothSame or MergeSectionKind.Stable => Right,
        _ => throw new InvalidOperationException("Conflict sections are not resolved."),
    };
}

public sealed record MergeResult(IReadOnlyList<MergeSection> Sections)
{
    public int Conflicts => Sections.Count(s => s.Kind == MergeSectionKind.Conflict);
    public bool Clean => Conflicts == 0;

    /// <summary>把结果写成文本：冲突段以 git 风格标记包围（&lt;&lt;&lt;&lt;&lt;&lt;&lt; 左 / ======= / &gt;&gt;&gt;&gt;&gt;&gt;&gt; 右）。</summary>
    public string Compose(string leftLabel = "LEFT", string rightLabel = "RIGHT", string newLine = "\n")
    {
        var text = new System.Text.StringBuilder();
        foreach (var section in Sections)
        {
            if (section.Kind == MergeSectionKind.Conflict)
            {
                text.Append("<<<<<<< ").Append(leftLabel).Append(newLine);
                foreach (string line in section.Left) text.Append(line).Append(newLine);
                text.Append("=======").Append(newLine);
                foreach (string line in section.Right) text.Append(line).Append(newLine);
                text.Append(">>>>>>> ").Append(rightLabel).Append(newLine);
            }
            else foreach (string line in section.Resolved) text.Append(line).Append(newLine);
        }
        return text.ToString();
    }
}
