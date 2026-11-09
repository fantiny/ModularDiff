using ModularDiff.Contracts;

namespace ModularDiff.Core;

/// <summary>diff3-style three-way merge over lines: the base anchors both sides; changes in
/// disjoint base ranges merge automatically (each side wins its own region), overlapping changes
/// compare against the base — identical outcomes collapse, divergent ones become conflicts.</summary>
public static class ThreeWayMerger
{
    /// <param name="baseLines">common ancestor lines (no line endings).</param>
    /// <param name="leftLines">this side's lines.</param>
    /// <param name="rightLines">the other side's lines.</param>
    public static MergeResult Merge(IReadOnlyList<string> baseLines, IReadOnlyList<string> leftLines, IReadOnlyList<string> rightLines,
        string algorithmId, IPluginResolver plugins, CancellationToken cancellationToken = default)
    {
        _ = plugins.Get<IDiffAlgorithm>(algorithmId);
        var left = Track(baseLines, leftLines, algorithmId, plugins, cancellationToken);
        var right = Track(baseLines, rightLines, algorithmId, plugins, cancellationToken);
        var sections = new List<MergeSection>();
        int position = 0, l = 0, r = 0;
        while (l < left.Chunks.Count || r < right.Chunks.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int leftStart = l < left.Chunks.Count ? left.Chunks[l].BaseStart : int.MaxValue;
            int rightStart = r < right.Chunks.Count ? right.Chunks[r].BaseStart : int.MaxValue;
            if (leftStart == int.MaxValue && rightStart == int.MaxValue) break;
            if (leftStart < rightStart)
            {
                EmitStable(sections, baseLines, position, leftStart);
                var chunk = left.Chunks[l++];
                sections.Add(new(MergeSectionKind.AutoLeft, Slice(leftLines, chunk.SideStart, chunk.SideEnd), []));
                position = chunk.BaseEnd;
                continue;
            }
            if (rightStart < leftStart)
            {
                EmitStable(sections, baseLines, position, rightStart);
                var chunk = right.Chunks[r++];
                sections.Add(new(MergeSectionKind.AutoRight, [], Slice(rightLines, chunk.SideStart, chunk.SideEnd)));
                position = chunk.BaseEnd;
                continue;
            }
            // Overlap window: grows until no chunk of either side starts at or before its end,
            // so the window boundaries are stable positions on both sides.
            int windowStart = leftStart, windowEnd = Math.Max(left.Chunks[l].BaseEnd, right.Chunks[r].BaseEnd);
            bool grew = true;
            while (grew)
            {
                grew = false;
                while (l < left.Chunks.Count && left.Chunks[l].BaseStart <= windowEnd) { windowEnd = Math.Max(windowEnd, left.Chunks[l].BaseEnd); l++; grew = true; }
                while (r < right.Chunks.Count && right.Chunks[r].BaseStart <= windowEnd) { windowEnd = Math.Max(windowEnd, right.Chunks[r].BaseEnd); r++; grew = true; }
            }
            EmitStable(sections, baseLines, position, windowStart);
            string[] leftVersion = left.Slice(windowStart, windowEnd);
            string[] rightVersion = right.Slice(windowStart, windowEnd);
            string[] baseVersion = Slice(baseLines, windowStart, windowEnd);
            if (leftVersion.SequenceEqual(rightVersion))
                sections.Add(new(MergeSectionKind.BothSame, leftVersion, rightVersion));
            else if (rightVersion.SequenceEqual(baseVersion))
                sections.Add(new(MergeSectionKind.AutoLeft, leftVersion, []));
            else if (leftVersion.SequenceEqual(baseVersion))
                sections.Add(new(MergeSectionKind.AutoRight, [], rightVersion));
            else
                sections.Add(new(MergeSectionKind.Conflict, leftVersion, rightVersion));
            position = windowEnd;
        }
        EmitStable(sections, baseLines, position, baseLines.Count);
        return new(sections);
    }

    private static string[] Slice(IReadOnlyList<string> lines, int from, int to)
    {
        var result = new string[Math.Max(0, to - from)];
        for (int i = 0; i < result.Length; i++) result[i] = lines[from + i];
        return result;
    }

    private static void EmitStable(List<MergeSection> sections, IReadOnlyList<string> baseLines, int from, int to)
    {
        if (to <= from) return;
        string[] lines = Slice(baseLines, from, to);
        sections.Add(new(MergeSectionKind.Stable, lines, lines));
    }

    private readonly record struct Chunk(int BaseStart, int BaseEnd, int SideStart, int SideEnd);

    /// <summary>One side against the base: changed base ranges, plus boundary[] mapping every base
    /// position (0..N) to the side-line position at that boundary — exact slicing for any window.</summary>
    private sealed class SideTrack(List<Chunk> chunks, int[] boundary, IReadOnlyList<string> sideLines)
    {
        public List<Chunk> Chunks => chunks;
        public string[] Slice(int baseStart, int baseEnd)
        {
            int start = Edge(baseStart, start: true);
            int end = Math.Max(Edge(baseEnd, start: false), start);
            return ThreeWayMerger.Slice(sideLines, start, end);
        }
        /// <summary>Chunk edges win at window boundaries (insertions share the base position with
        /// the following stable line, so a single boundary[] value is ambiguous there).</summary>
        private int Edge(int position, bool start)
        {
            foreach (var chunk in Chunks)
            {
                if (start && chunk.BaseStart == position) return chunk.SideStart;
                if (!start && chunk.BaseEnd == position) return chunk.SideEnd;
            }
            for (int i = Math.Clamp(position, 0, boundary.Length - 1); i >= 0; i--)
                if (boundary[i] >= 0) return boundary[i];
            return 0;
        }
    }

    private static SideTrack Track(IReadOnlyList<string> baseLines, IReadOnlyList<string> sideLines,
        string algorithmId, IPluginResolver plugins, CancellationToken token)
    {
        var algorithm = plugins.Get<IDiffAlgorithm>(algorithmId);
        var diff = algorithm.Compare(baseLines, sideLines, plugins, token);
        var boundary = new int[baseLines.Count + 1];
        Array.Fill(boundary, -1);
        var chunks = new List<Chunk>();
        int baseIndex = 0, sideIndex = 0, runBaseStart = -1, runSideStart = 0;
        void Close(int nextBase, int nextSide)
        {
            if (runBaseStart < 0) return;
            chunks.Add(new(runBaseStart, nextBase, runSideStart, nextSide));
            runBaseStart = -1;
        }
        foreach (var edit in diff.Edits)
        {
            token.ThrowIfCancellationRequested();
            if (edit.Kind == EditKind.Equal)
            {
                Close(edit.LeftIndex!.Value, edit.RightIndex!.Value);
                baseIndex = edit.LeftIndex.Value + 1;
                sideIndex = edit.RightIndex.Value + 1;
                boundary[edit.LeftIndex.Value] = edit.RightIndex.Value;
            }
            else
            {
                if (runBaseStart < 0)
                {
                    runBaseStart = baseIndex;
                    runSideStart = sideIndex;
                }
                if (edit.Kind == EditKind.Delete) baseIndex = edit.LeftIndex!.Value + 1;
                else sideIndex = edit.RightIndex!.Value + 1;
            }
        }
        Close(baseLines.Count, sideLines.Count);
        boundary[baseLines.Count] = sideLines.Count;
        return new SideTrack(chunks, boundary, sideLines);
    }
}
