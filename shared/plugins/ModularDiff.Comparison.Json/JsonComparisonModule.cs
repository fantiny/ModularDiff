using System.Text.Json;
using System.Text.Json.Nodes;
using ModularDiff.Contracts;

namespace ModularDiff.Comparison.Json;

/// <summary>Structural JSON comparison: parses both files, canonicalizes (sorted keys, consistent
/// formatting), then aligns line-by-line — key order and whitespace differences are ignored,
/// value differences surface cleanly. Malformed JSON falls back to the files module's text path.</summary>
public sealed class JsonComparisonModule : IComparisonModule
{
    public string Id => "json-structural";
    public string DisplayName => "JSON · structural (key-sorted)";
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".json", ".jsonc" };

    public bool CanCompare(string leftPath, string rightPath) =>
        Extensions.Contains(Path.GetExtension(leftPath)) && Extensions.Contains(Path.GetExtension(rightPath))
        && File.Exists(leftPath) && File.Exists(rightPath);

    public async Task<ComparisonResult> CompareAsync(ComparisonRequest request, IPluginResolver plugins,
        IProgress<ComparisonProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var algorithm = plugins.Get<IDiffAlgorithm>(request.AlgorithmId);
        string leftText = await File.ReadAllTextAsync(request.LeftPath, cancellationToken);
        string rightText = await File.ReadAllTextAsync(request.RightPath, cancellationToken);

        string[] leftLines, rightLines;
        string? note = null;
        try
        {
            var leftNode = JsonNode.Parse(leftText) ?? JsonValue.Create(string.Empty);
            var rightNode = JsonNode.Parse(rightText) ?? JsonValue.Create(string.Empty);
            leftLines = Canonical(leftNode).Split('\n');
            rightLines = Canonical(rightNode).Split('\n');
        }
        catch (JsonException)
        {
            // Malformed JSON on either side: fall back to raw text lines so the user can see the error.
            leftLines = leftText.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            rightLines = rightText.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            note = "Invalid JSON on at least one side; comparing as plain text.";
        }

        var options = request.EffectiveOptions;
        var unimportant = new List<System.Text.RegularExpressions.Regex>();
        foreach (string pattern in options.EffectiveUnimportantPatterns)
            try { unimportant.Add(new(pattern, System.Text.RegularExpressions.RegexOptions.Compiled, TimeSpan.FromMilliseconds(100))); }
            catch (ArgumentException) { }

        string Key(string line)
        {
            var text = line;
            foreach (var pattern in unimportant) text = pattern.Replace(text, "");
            if (options.IgnoreWhitespace) text = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
            if (options.IgnoreCase) text = text.ToUpperInvariant();
            return text;
        }

        var diff = algorithm.Compare(leftLines.Select(Key).ToArray(), rightLines.Select(Key).ToArray(), plugins, cancellationToken);
        var lines = diff.Edits.Select(e => new DiffLine(e.Kind, e.LeftIndex + 1, e.RightIndex + 1,
            e.LeftIndex is int i ? leftLines[i] : null,
            e.RightIndex is int j ? rightLines[j] : null, "LF", "LF")).ToArray();
        bool changed = lines.Any(l => l.Kind != EditKind.Equal);
        var file = new FileComparison(Path.GetFileName(request.LeftPath),
            changed ? FileStatus.Modified : FileStatus.Equal, lines, false, diff.IsApproximate,
            note ?? (changed ? null : "JSON structures are identical (key-sorted canonical comparison)."));
        progress?.Report(new(1, 1, file.RelativePath));
        return new(request.LeftPath, request.RightPath, Id, algorithm.Id, [file], []);
    }

    /// <summary>Canonical form: keys sorted recursively, 2-space indent, built as a string
    /// directly from read-only traversal (no tree mutation, no parent-reference issues).</summary>
    private static string Canonical(JsonNode node)
    {
        var sb = new System.Text.StringBuilder();
        WriteCanonical(sb, node, 0);
        return sb.ToString();
    }

    private static void WriteCanonical(System.Text.StringBuilder sb, JsonNode? node, int depth)
    {
        string indent = new string(' ', depth * 2);
        string inner = new string(' ', (depth + 1) * 2);
        if (node is JsonObject obj)
        {
            var pairs = obj.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
            if (pairs.Count == 0) { sb.Append("{}"); return; }
            sb.Append("{\n");
            for (int i = 0; i < pairs.Count; i++)
            {
                sb.Append(inner).Append('"').Append(Escape(pairs[i].Key)).Append("\": ");
                WriteCanonical(sb, pairs[i].Value, depth + 1);
                if (i < pairs.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append(indent).Append('}');
        }
        else if (node is JsonArray array)
        {
            if (array.Count == 0) { sb.Append("[]"); return; }
            sb.Append("[\n");
            for (int i = 0; i < array.Count; i++)
            {
                sb.Append(inner);
                WriteCanonical(sb, array[i], depth + 1);
                if (i < array.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append(indent).Append(']');
        }
        else
        {
            sb.Append(node?.ToJsonString() ?? "null");
        }
    }

    private static string Escape(string key) => key.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
}
