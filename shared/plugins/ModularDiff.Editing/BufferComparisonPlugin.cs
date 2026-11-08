using ModularDiff.Contracts;

namespace ModularDiff.Editing;

public sealed class BufferComparisonPlugin : IBufferComparison
{
    public string Id => "buffer-comparison";
    public string DisplayName => "Unsaved buffer comparison";
    public BufferComparison Compare(BufferSnapshot left, BufferSnapshot right, string algorithmId, ComparisonOptions options,
        IPluginResolver plugins, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var a = DocumentLines.Parse(left.Text); var b = DocumentLines.Parse(right.Text);
        string Key(DocumentLine line)
        {
            string text = line.Text;
            if (options.IgnoreWhitespace) text = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
            if (options.IgnoreCase) text = text.ToUpperInvariant();
            return options.IgnoreLineEndings ? text : text + line.Ending;
        }
        var diff = plugins.Get<IDiffAlgorithm>(algorithmId).Compare(a.Select(Key).ToArray(), b.Select(Key).ToArray(), plugins, cancellationToken);
        var lines = diff.Edits.Select(e => new DiffLine(e.Kind, e.LeftIndex + 1, e.RightIndex + 1,
            e.LeftIndex is int x ? a[x].Text : null, e.RightIndex is int y ? b[y].Text : null,
            e.LeftIndex is int i ? a[i].EndingLabel : null, e.RightIndex is int j ? b[j].EndingLabel : null)).ToArray();
        return new(left.Version, right.Version, new(System.IO.Path.GetFileName(left.Path),
            lines.Any(l => l.Kind != EditKind.Equal) ? FileStatus.Modified : FileStatus.Equal, lines, IsApproximate: diff.IsApproximate, Note: diff.Note));
    }
}
