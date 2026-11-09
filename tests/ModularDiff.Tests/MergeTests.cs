using ModularDiff.Contracts;
using ModularDiff.Core;

internal static class MergeTests
{
    public static async Task Run(Func<string, Func<Task>, Task> test) => await test("Three-way merge: auto-merge, both-same, conflicts, delete-vs-edit", () =>
    {
        MergeResult Merge(params string[][] sides) =>
            ThreeWayMerger.Merge(sides[0], sides[1], sides[2], "myers", Catalog!);

        // Disjoint edits from both sides merge automatically.
        var result = Merge(
            ["a", "b", "c", "d"],
            ["A", "b", "c", "d"],
            ["a", "b", "C", "d"]);
        Check(result.Clean && result.Compose() == "A\nb\nC\nd\n", "disjoint edits merge: " + result.Compose().Replace("\n", "|"));
        Check(result.Sections.Count(s => s.Kind == MergeSectionKind.AutoLeft) == 1
              && result.Sections.Count(s => s.Kind == MergeSectionKind.AutoRight) == 1, "section kinds");

        // Both changed the same way: one copy.
        result = Merge(["a", "b"], ["a", "X"], ["a", "X"]);
        Check(result.Clean && result.Compose() == "a\nX\n" && result.Sections.Count(s => s.Kind == MergeSectionKind.BothSame) == 1, "both-same collapses");

        // Both changed differently: conflict carrying both versions.
        result = Merge(["line"], ["left version"], ["right version"]);
        Check(result.Conflicts == 1, "diverging edits conflict");
        Check(result.Sections.Single(s => s.Kind == MergeSectionKind.Conflict).Left.SequenceEqual(["left version"])
              && result.Sections.Single(s => s.Kind == MergeSectionKind.Conflict).Right.SequenceEqual(["right version"]), "conflict carries both sides");
        string composed = result.Compose("OURS", "THEIRS");
        Check(composed.Contains("<<<<<<< OURS") && composed.Contains("left version") && composed.Contains("=======") && composed.Contains("right version") && composed.Contains(">>>>>>> THEIRS"), "git-style markers: " + composed.Replace("\n", "|"));

        // Delete vs edit of the same line: conflict (delete counts as a change).
        result = Merge(["keep", "gone", "tail"], ["keep", "tail"], ["keep", "edited", "tail"]);
        Check(result.Conflicts == 1, "delete vs edit conflicts, conflicts=" + result.Conflicts);

        // One side unchanged: fast-forward to the other side.
        result = Merge(["a", "b"], ["a", "b"], ["a", "B2", "extra"]);
        Check(result.Clean && result.Compose() == "a\nB2\nextra\n", "fast-forward");

        // Insertions at the same spot with different content: conflict.
        result = Merge(["x"], ["top1", "x"], ["top2", "x"]);
        Check(result.Conflicts == 1, "same-spot insertions conflict");

        // Insertions at different spots: both kept.
        result = Merge(["a", "b"], ["L", "a", "b"], ["a", "b", "R"]);
        Check(result.Clean && result.Compose() == "L\na\nb\nR\n", "insertions on both ends");

        // Random property: every non-conflicting edit of either side survives the merge.
        var random = new Random(20261109);
        for (int trial = 0; trial < 300; trial++)
        {
            var b = RandomLines(random);
            var left = Mutate(b, random);
            var right = Mutate(b, random);
            var merged = ThreeWayMerger.Merge(b, left, right, "myers", Catalog!);
            string text = merged.Compose(newLine: "\n");
            foreach (string line in left.Where(l => !b.Contains(l) && !right.Contains(l)))
                if (!text.Contains(line)) Check(false, $"left-only line '{line}' lost (trial {trial})");
            foreach (string line in right.Where(l => !b.Contains(l) && !left.Contains(l)))
                if (!text.Contains(line)) Check(false, $"right-only line '{line}' lost (trial {trial})");
        }
        return Task.CompletedTask;
    });

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static IPluginResolver? Catalog { get; set; }
    private static List<string> RandomLines(Random random) =>
        [.. Enumerable.Range(0, random.Next(0, 12)).Select(_ => "l" + random.Next(30))];
    private static List<string> Mutate(List<string> lines, Random random)
    {
        var result = new List<string>(lines);
        for (int i = 0; i < random.Next(0, 4); i++)
        {
            if (result.Count == 0 || random.Next(3) == 0) result.Insert(random.Next(result.Count + 1), "n" + random.Next(1000));
            else if (random.Next(2) == 0) result.RemoveAt(random.Next(result.Count));
            else result[random.Next(result.Count)] = "m" + random.Next(1000);
        }
        return result;
    }
}
