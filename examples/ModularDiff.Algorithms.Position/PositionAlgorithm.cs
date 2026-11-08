using ModularDiff.Contracts;

namespace ModularDiff.Algorithms.Position;

// A deliberately simple third-party example. Build/copy this package without rebuilding either host.
public sealed class PositionAlgorithm : IDiffAlgorithm
{
    public string Id => "position";
    public string DisplayName => "Position · extension example";
    public AlgorithmResult Compare(IReadOnlyList<string> left, IReadOnlyList<string> right,
        IPluginResolver plugins, CancellationToken cancellationToken = default)
    {
        var edits = new List<Edit>();
        for (int i = 0; i < Math.Max(left.Count, right.Count); i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (i < left.Count && i < right.Count && left[i] == right[i]) edits.Add(new(EditKind.Equal, i, i));
            else
            {
                if (i < left.Count) edits.Add(new(EditKind.Delete, i, null));
                if (i < right.Count) edits.Add(new(EditKind.Insert, null, i));
            }
        }
        return new(edits, Note: "Positional comparison does not realign inserted/deleted lines.");
    }
}
