using ModularDiff.Contracts;

namespace ModularDiff.Algorithms.Myers;

public sealed class MyersAlgorithm : IDiffAlgorithm
{
    public string Id => "myers";
    public string DisplayName => "Myers · shortest edit script";
    // Bound trace storage; an adversarial file must not exhaust all memory.
    private const long MaxTraceCells = 8_000_000;

    public AlgorithmResult Compare(IReadOnlyList<string> left, IReadOnlyList<string> right,
        IPluginResolver plugins, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int prefix = 0, suffix = 0;
        while (prefix < Math.Min(left.Count, right.Count) && left[prefix] == right[prefix])
        {
            if ((prefix & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            prefix++;
        }
        while (suffix < Math.Min(left.Count, right.Count) - prefix && left[left.Count - 1 - suffix] == right[right.Count - 1 - suffix])
        {
            if ((suffix & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            suffix++;
        }
        bool approximate = false;
        var edits = new List<Edit>();
        for (int i = 0; i < prefix; i++) edits.Add(new(EditKind.Equal, i, i));
        edits.AddRange(Solve(left, right, prefix, prefix, left.Count - prefix - suffix, right.Count - prefix - suffix, cancellationToken, ref approximate));
        for (int i = suffix; i > 0; i--) edits.Add(new(EditKind.Equal, left.Count - i, right.Count - i));
        return new(edits, approximate, approximate ? "Myers trace budget exceeded; anchored on unique common lines (not minimal)." : null);
    }

    private static List<Edit> Solve(IReadOnlyList<string> left, IReadOnlyList<string> right, int lx, int rx,
        int n, int m, CancellationToken token, ref bool approximate)
    {
        List<Edit> Coarse()
        {
            var edits = new List<Edit>(n + m);
            for (int i = 0; i < n; i++) { if ((i & 1023) == 0) token.ThrowIfCancellationRequested(); edits.Add(new(EditKind.Delete, lx + i, null)); }
            for (int j = 0; j < m; j++) { if ((j & 1023) == 0) token.ThrowIfCancellationRequested(); edits.Add(new(EditKind.Insert, null, rx + j)); }
            return edits;
        }
        if (n == 0 || m == 0) return Coarse();
        int max = checked(n + m);
        if (max > 200_000) { approximate = true; return Anchored(left, right, lx, rx, n, m, token, ref approximate); }
        int offset = max + 1;
        var frontier = new int[2 * max + 3];
        var trace = new List<int[]>();
        for (int d = 0; d <= max; d++)
        {
            token.ThrowIfCancellationRequested();
            if ((long)(trace.Count + 1) * frontier.Length > MaxTraceCells)
            { approximate = true; return Anchored(left, right, lx, rx, n, m, token, ref approximate); }
            trace.Add((int[])frontier.Clone());
            for (int k = -d; k <= d; k += 2)
            {
                if ((k & 1023) == 0) token.ThrowIfCancellationRequested();
                int x = k == -d || (k != d && frontier[offset + k - 1] < frontier[offset + k + 1])
                    ? frontier[offset + k + 1] : frontier[offset + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && left[lx + x] == right[rx + y])
                { if ((x & 1023) == 0) token.ThrowIfCancellationRequested(); x++; y++; }
                frontier[offset + k] = x;
                if (x >= n && y >= m)
                    return Backtrack(trace, offset, n, m, token)
                        .Select(e => new Edit(e.Kind, e.LeftIndex is int li ? lx + li : null, e.RightIndex is int ri ? rx + ri : null))
                        .ToList();
            }
        }
        throw new InvalidOperationException("No edit path found.");
    }

    /// <summary>预算超限后的兜底:按"两侧均唯一且相同"的行锚定,锚点间递归再解,子区间仍超限则继续锚定;
    /// 这样相同的唯一行(如逐行拆开的 CSS 变量)不会被退化为 delete/insert,只有真正的无锚大区间才整体对撞。</summary>
    private static List<Edit> Anchored(IReadOnlyList<string> left, IReadOnlyList<string> right, int lx, int rx,
        int n, int m, CancellationToken token, ref bool approximate)
    {
        var leftCount = new Dictionary<string, int>();
        for (int i = 0; i < n; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            Increment(leftCount, left[lx + i]);
        }
        var rightCount = new Dictionary<string, int>();
        var rightIndex = new Dictionary<string, int>();
        for (int j = 0; j < m; j++)
        {
            if ((j & 1023) == 0) token.ThrowIfCancellationRequested();
            string line = right[rx + j];
            Increment(rightCount, line);
            rightIndex[line] = j;
        }
        var anchors = new List<(int X, int Y)>();
        int scan = 0;
        for (int x = 0; x < n; x++)
        {
            string line = left[lx + x];
            if (leftCount[line] == 1 && rightCount.TryGetValue(line, out int count) && count == 1 && rightIndex[line] >= scan)
            {
                anchors.Add((x, rightIndex[line]));
                scan = rightIndex[line] + 1;
            }
        }
        if (anchors.Count == 0)
        {
            var coarse = new List<Edit>(n + m);
            for (int i = 0; i < n; i++) { if ((i & 1023) == 0) token.ThrowIfCancellationRequested(); coarse.Add(new(EditKind.Delete, lx + i, null)); }
            for (int j = 0; j < m; j++) { if ((j & 1023) == 0) token.ThrowIfCancellationRequested(); coarse.Add(new(EditKind.Insert, null, rx + j)); }
            return coarse;
        }
        var edits = new List<Edit>();
        int px = 0, py = 0;
        foreach (var (ax, ay) in anchors)
        {
            if (ax > px || ay > py)
                edits.AddRange(Solve(left, right, lx + px, rx + py, ax - px, ay - py, token, ref approximate));
            edits.Add(new(EditKind.Equal, lx + ax, rx + ay));
            px = ax + 1; py = ay + 1;
        }
        if (px < n || py < m)
            edits.AddRange(Solve(left, right, lx + px, rx + py, n - px, m - py, token, ref approximate));
        return edits;
    }
    private static void Increment(Dictionary<string, int> counts, string line)
    { counts.TryGetValue(line, out int value); counts[line] = value + 1; }

    private static List<Edit> Backtrack(List<int[]> trace, int offset, int x, int y, CancellationToken token)
    {
        var edits = new List<Edit>();
        for (int d = trace.Count - 1; d >= 0; d--)
        {
            token.ThrowIfCancellationRequested();
            var v = trace[d];
            int k = x - y;
            int prevK = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? k + 1 : k - 1;
            int prevX = v[offset + prevK], prevY = prevX - prevK;
            while (x > prevX && y > prevY) { x--; y--; edits.Add(new(EditKind.Equal, x, y)); }
            if (d == 0) break;
            if (x == prevX) { y--; edits.Add(new(EditKind.Insert, null, y)); }
            else { x--; edits.Add(new(EditKind.Delete, x, null)); }
        }
        edits.Reverse();
        return edits;
    }
}
