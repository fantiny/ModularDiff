using ModularDiff.Contracts;

namespace ModularDiff.Editing;

public sealed class BlockMergePlugin : IBlockMerge
{
    public string Id => "block-merge";
    public string DisplayName => "Bidirectional block merge";
    public TextEdit Plan(BufferComparison comparison, BufferSnapshot left, BufferSnapshot right, int hunkId, MergeDirection direction)
    {
        if (left.Version != comparison.LeftVersion || right.Version != comparison.RightVersion)
            throw new InvalidOperationException("差异已经过期，请等待重比较完成再复制。");
        int a = 0, b = 0, hunk = 0;
        for (int i = 0; i < comparison.File.Lines.Count;)
        {
            var line = comparison.File.Lines[i];
            if (line.Kind == EditKind.Equal) { a++; b++; i++; continue; }
            int startA = a, startB = b;
            while (i < comparison.File.Lines.Count && comparison.File.Lines[i].Kind != EditKind.Equal)
            {
                line = comparison.File.Lines[i++];
                if (line.LeftLine is not null) a++; if (line.RightLine is not null) b++;
            }
            if (hunk++ != hunkId) continue;
            bool toRight = direction == MergeDirection.LeftToRight;
            var source = toRight ? left : right; var target = toRight ? right : left;
            var sourceLines = DocumentLines.Parse(source.Text); var targetLines = DocumentLines.Parse(target.Text);
            int sourceStart = toRight ? startA : startB, sourceEnd = toRight ? a : b;
            int targetStart = toRight ? startB : startA, targetEnd = toRight ? b : a;
            static int Offset(IReadOnlyList<DocumentLine> lines, int index, string text) => index < lines.Count ? lines[index].Offset : text.Length;
            int from = Offset(sourceLines, sourceStart, source.Text), until = Offset(sourceLines, sourceEnd, source.Text);
            int offset = Offset(targetLines, targetStart, target.Text), end = Offset(targetLines, targetEnd, target.Text);
            string insertion = source.Text[from..until];
            string ending = targetLines.FirstOrDefault(l => l.Ending.Length > 0)?.Ending ?? "\n";
            // Ignoring EOL can align a non-terminated last line with a terminated source line.
            // Inserting after it must add a separator instead of concatenating two logical lines.
            if (insertion.Length > 0 && offset > 0 && target.Text[offset - 1] is not ('\r' or '\n')) insertion = ending + insertion;
            if (insertion.Length > 0 && end < target.Text.Length && insertion[^1] is not ('\r' or '\n')) insertion += ending;
            return new(offset, end - offset, insertion, toRight ? "Copy block left → right" : "Copy block right → left");
        }
        throw new ArgumentOutOfRangeException(nameof(hunkId), "请选择一个差异块。");
    }
}
