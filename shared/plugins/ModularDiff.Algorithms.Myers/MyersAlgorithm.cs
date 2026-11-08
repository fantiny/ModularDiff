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
        int n = left.Count - prefix - suffix, m = right.Count - prefix - suffix;
        var output = new List<Edit>();
        for (int i = 0; i < prefix; i++) output.Add(new(EditKind.Equal, i, i));
        bool approximate = false;
        var middle = Solve(left, right, prefix, n, m, cancellationToken, ref approximate);
        output.AddRange(middle.Select(e => new Edit(e.Kind, e.LeftIndex + prefix, e.RightIndex + prefix)));
        for (int i = suffix; i > 0; i--) output.Add(new(EditKind.Equal, left.Count - i, right.Count - i));
        return new(output, approximate, approximate ? "Myers trace budget exceeded; unmatched region shown as delete/insert (not minimal)." : null);
    }

    private static List<Edit> Solve(IReadOnlyList<string> left, IReadOnlyList<string> right, int start,
        int n, int m, CancellationToken token, ref bool approximate)
    {
        List<Edit> Coarse()
        {
            var edits = new List<Edit>(n + m);
            for (int i = 0; i < n; i++) { if ((i & 1023) == 0) token.ThrowIfCancellationRequested(); edits.Add(new(EditKind.Delete, i, null)); }
            for (int j = 0; j < m; j++) { if ((j & 1023) == 0) token.ThrowIfCancellationRequested(); edits.Add(new(EditKind.Insert, null, j)); }
            return edits;
        }
        if (n == 0 || m == 0) return Coarse();
        int max = checked(n + m);
        if (max > 200_000) { approximate = true; return Coarse(); }
        int offset = max + 1;
        var frontier = new int[2 * max + 3];
        var trace = new List<int[]>();
        for (int d = 0; d <= max; d++)
        {
            token.ThrowIfCancellationRequested();
            if ((long)(trace.Count + 1) * frontier.Length > MaxTraceCells)
            { approximate = true; return Coarse(); }
            trace.Add((int[])frontier.Clone());
            for (int k = -d; k <= d; k += 2)
            {
                if ((k & 1023) == 0) token.ThrowIfCancellationRequested();
                int x = k == -d || (k != d && frontier[offset + k - 1] < frontier[offset + k + 1])
                    ? frontier[offset + k + 1] : frontier[offset + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && left[start + x] == right[start + y])
                { if ((x & 1023) == 0) token.ThrowIfCancellationRequested(); x++; y++; }
                frontier[offset + k] = x;
                if (x >= n && y >= m) return Backtrack(trace, offset, n, m, token);
            }
        }
        throw new InvalidOperationException("No edit path found.");
    }

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
