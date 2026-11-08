using ModularDiff.Contracts;

namespace ModularDiff.Algorithms.Patience;

public sealed class PatienceAlgorithm : IDiffAlgorithm
{
    public string Id => "patience";
    public string DisplayName => "Patience · unique-line anchors";
    public IReadOnlyList<string> Dependencies => ["myers"];

    public AlgorithmResult Compare(IReadOnlyList<string> left, IReadOnlyList<string> right,
        IPluginResolver plugins, CancellationToken cancellationToken = default)
    {
        // Dependency is resolved through the contract, not a reference to the Myers assembly.
        var fallback = plugins.Get<IDiffAlgorithm>("myers");
        var edits = new List<Edit>();
        bool approximate = false;
        Visit(0, left.Count, 0, right.Count, 0);
        return new(edits, approximate, approximate ? "A Patience gap exceeded the Myers budget; that region uses coarse edits." : null);

        void Visit(int a0, int a1, int b0, int b1, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (a0 < a1 && b0 < b1 && left[a0] == right[b0])
            { if ((a0 & 1023) == 0) cancellationToken.ThrowIfCancellationRequested(); edits.Add(new(EditKind.Equal, a0++, b0++)); }
            int suffix = 0;
            while (a0 < a1 && b0 < b1 && left[a1 - 1] == right[b1 - 1])
            { if ((suffix & 1023) == 0) cancellationToken.ThrowIfCancellationRequested(); a1--; b1--; suffix++; }
            if (a0 == a1 || b0 == b1)
            {
                for (int i = a0; i < a1; i++) edits.Add(new(EditKind.Delete, i, null));
                for (int i = b0; i < b1; i++) edits.Add(new(EditKind.Insert, null, i));
            }
            else
            {
                var anchors = depth < 128 ? FindAnchors(a0, a1, b0, b1) : [];
                if (anchors.Count == 0)
                {
                    var result = fallback.Compare(left.Skip(a0).Take(a1 - a0).ToArray(), right.Skip(b0).Take(b1 - b0).ToArray(), plugins, cancellationToken);
                    approximate |= result.IsApproximate;
                    edits.AddRange(result.Edits.Select(e => new Edit(e.Kind, e.LeftIndex + a0, e.RightIndex + b0)));
                }
                else
                {
                    int a = a0, b = b0;
                    foreach (var (x, y) in anchors)
                    {
                        Visit(a, x, b, y, depth + 1);
                        edits.Add(new(EditKind.Equal, x, y)); a = x + 1; b = y + 1;
                    }
                    Visit(a, a1, b, b1, depth + 1);
                }
            }
            for (int i = 0; i < suffix; i++) edits.Add(new(EditKind.Equal, a1 + i, b1 + i));
        }

        List<(int, int)> FindAnchors(int a0, int a1, int b0, int b1)
        {
            Dictionary<string, (int Count, int Index)> Count(IReadOnlyList<string> lines, int begin, int end)
            {
                var counts = new Dictionary<string, (int Count, int Index)>(StringComparer.Ordinal);
                for (int i = begin; i < end; i++)
                {
                    if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                    counts[lines[i]] = counts.TryGetValue(lines[i], out var old) ? (old.Count + 1, i) : (1, i);
                }
                return counts;
            }
            var a = Count(left, a0, a1); var b = Count(right, b0, b1);
            var pairs = a.Where(p => p.Value.Count == 1 && b.TryGetValue(p.Key, out var v) && v.Count == 1)
                .Select(p => (X: p.Value.Index, Y: b[p.Key].Index)).OrderBy(p => p.X).ToArray();
            if (pairs.Length == 0) return [];
            var tails = new int[pairs.Length]; var previous = new int[pairs.Length]; int size = 0;
            for (int i = 0; i < pairs.Length; i++)
            {
                if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                int lo = 0, hi = size;
                while (lo < hi) { int mid = (lo + hi) / 2; if (pairs[tails[mid]].Y < pairs[i].Y) lo = mid + 1; else hi = mid; }
                previous[i] = lo > 0 ? tails[lo - 1] : -1; tails[lo] = i; if (lo == size) size++;
            }
            var result = new List<(int, int)>();
            for (int i = tails[size - 1]; i >= 0; i = previous[i]) result.Add(pairs[i]);
            result.Reverse(); return result;
        }
    }
}
