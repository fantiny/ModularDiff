using System.Text;
using System.Text.RegularExpressions;
using ModularDiff.Contracts;

namespace ModularDiff.Comparison.Files;

public sealed class FileComparisonModule : IComparisonModule
{
    public string Id => "files";
    public string DisplayName => "Files · text / binary";
    public bool CanCompare(string leftPath, string rightPath) => File.Exists(leftPath) && File.Exists(rightPath);

    public async Task<ComparisonResult> CompareAsync(ComparisonRequest request, IPluginResolver plugins,
        IProgress<ComparisonProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var options = request.EffectiveOptions;
        var algorithm = plugins.Get<IDiffAlgorithm>(request.AlgorithmId);
        var unimportant = new List<Regex>();
        var patternWarnings = new List<string>();
        foreach (string pattern in options.EffectiveUnimportantPatterns)
        {
            try { unimportant.Add(new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromMilliseconds(100))); }
            catch (ArgumentException) { patternWarnings.Add($"忽略无效的不重要差异规则：{pattern}"); }
        }
        await using var left = Open(request.LeftPath);
        await using var right = Open(request.RightPath);
        bool bytesEqual = await BytesEqual(left, right, cancellationToken);
        string name = Path.GetFileName(request.LeftPath);
        FileComparison file;
        if (Math.Max(left.Length, right.Length) > Math.Min(options.MaxTextBytes, int.MaxValue))
            file = new(name, bytesEqual ? FileStatus.Equal : FileStatus.Modified, [], true,
                Note: "Above text-size limit: exact byte comparison without line edits or text ignore options.");
        else
        {
            left.Position = right.Position = 0;
            var a = new byte[(int)left.Length]; var b = new byte[(int)right.Length];
            await left.ReadExactlyAsync(a, cancellationToken); await right.ReadExactlyAsync(b, cancellationToken);
            if (!TryDecode(a, out var leftText) || !TryDecode(b, out var rightText))
                file = new(name, bytesEqual ? FileStatus.Equal : FileStatus.Modified, [], true,
                    Note: "Binary or unsupported encoding: compared bytes exactly.");
            else
            {
                var leftLines = Split(leftText); var rightLines = Split(rightText);
                string Key(TextLine line)
                {
                    var text = line.Text;
                    foreach (var pattern in unimportant) text = pattern.Replace(text, "");
                    if (options.IgnoreWhitespace) text = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
                    if (options.IgnoreCase) text = text.ToUpperInvariant();
                    return options.IgnoreLineEndings ? text : text + line.Ending;
                }
                var diff = algorithm.Compare(leftLines.Select(Key).ToArray(), rightLines.Select(Key).ToArray(), plugins, cancellationToken);
                var lines = diff.Edits.Select(e => new DiffLine(e.Kind, e.LeftIndex + 1, e.RightIndex + 1,
                    e.LeftIndex is int i ? leftLines[i].Text : null, e.RightIndex is int j ? rightLines[j].Text : null,
                    e.LeftIndex is int x ? EndingLabel(leftLines[x].Ending) : null,
                    e.RightIndex is int y ? EndingLabel(rightLines[y].Ending) : null)).ToArray();
                bool changed = diff.Edits.Any(e => e.Kind != EditKind.Equal);
                file = new(name, changed ? FileStatus.Modified : FileStatus.Equal, lines, false, diff.IsApproximate,
                    diff.Note ?? (!bytesEqual && !changed ? "Text is equal under selected options; bytes/encoding may differ." : null));
            }
        }
        progress?.Report(new(1, 1, name));
        return new(request.LeftPath, request.RightPath, Id, algorithm.Id, [file], patternWarnings);
    }

    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        65536, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task<bool> BytesEqual(FileStream left, FileStream right, CancellationToken token)
    {
        if (left.Length != right.Length) return false;
        var a = new byte[65536]; var b = new byte[65536];
        long remaining = left.Length;
        while (remaining > 0)
        {
            int count = (int)Math.Min(a.Length, remaining);
            await left.ReadExactlyAsync(a.AsMemory(0, count), token);
            await right.ReadExactlyAsync(b.AsMemory(0, count), token);
            if (!a.AsSpan(0, count).SequenceEqual(b.AsSpan(0, count))) return false;
            remaining -= count;
        }
        return true;
    }

    private static bool TryDecode(byte[] bytes, out string text)
    {
        try
        {
            int skip = 0; Encoding encoding = new UTF8Encoding(false, true);
            if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) { encoding = new UTF32Encoding(false, false, true); skip = 4; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) { encoding = new UTF32Encoding(true, false, true); skip = 4; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, false, true); skip = 2; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, false, true); skip = 2; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) skip = 3;
            text = encoding.GetString(bytes, skip, bytes.Length - skip);
            return !text.Contains('\0');
        }
        catch (DecoderFallbackException) { text = ""; return false; }
    }

    private sealed record TextLine(string Text, string Ending);
    private static List<TextLine> Split(string text)
    {
        var lines = new List<TextLine>(); int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n')) continue;
            int end = i;
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            lines.Add(new(text[start..end], text[end..(i + 1)])); start = i + 1;
        }
        if (start < text.Length) lines.Add(new(text[start..], ""));
        return lines;
    }
    private static string EndingLabel(string ending) => ending switch { "\r\n" => "CRLF", "\n" => "LF", "\r" => "CR", _ => "EOF" };
}
