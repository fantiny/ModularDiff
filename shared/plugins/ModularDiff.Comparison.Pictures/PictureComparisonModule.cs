using System.Buffers.Binary;
using ModularDiff.Contracts;

namespace ModularDiff.Comparison.Pictures;

/// <summary>Compares picture files byte-exactly and reports pixel dimensions from the headers,
/// so the GUI can render them side by side with a pixel-difference overlay.</summary>
public sealed class PictureComparisonModule : IComparisonModule
{
    public string Id => "pictures";
    public string DisplayName => "Pictures · png / jpeg / bmp / gif";
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    public bool CanCompare(string leftPath, string rightPath) =>
        Extensions.Contains(Path.GetExtension(leftPath)) && Extensions.Contains(Path.GetExtension(rightPath))
        && File.Exists(leftPath) && File.Exists(rightPath);

    public async Task<ComparisonResult> CompareAsync(ComparisonRequest request, IPluginResolver plugins,
        IProgress<ComparisonProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        byte[] left = await File.ReadAllBytesAsync(request.LeftPath, cancellationToken);
        byte[] right = await File.ReadAllBytesAsync(request.RightPath, cancellationToken);
        var (leftFormat, leftSize) = Probe(left);
        var (rightFormat, rightSize) = Probe(right);
        var segments = Segments(left, right);
        string note = $"{leftFormat} {Describe(leftSize)} ↔ {rightFormat} {Describe(rightSize)}"
            + (segments.Count > 0 ? $" · {segments.Count} 段字节差异" : " · 字节完全一致");
        var file = new FileComparison(Path.GetFileName(request.LeftPath),
            segments.Count == 0 ? FileStatus.Equal : FileStatus.Modified, [], true, Note: note,
            BinaryDiffs: segments.Count == 0 ? null : segments);
        progress?.Report(new(1, 1, file.RelativePath));
        return new(request.LeftPath, request.RightPath, Id, request.AlgorithmId, [file], []);
    }

    private static string Describe((int Width, int Height)? size) =>
        size is { } s ? $"{s.Width}×{s.Height}" : "未知尺寸";

    /// <summary>Unequal byte segments with the same merge/cap rules as the files module.</summary>
    private static List<ByteDiff> Segments(byte[] left, byte[] right)
    {
        const long gap = 16;
        var segments = new List<ByteDiff>();
        long start = -1, end = -1;
        void Flush(long length) { if (start >= 0) { segments.Add(new(start, length)); start = -1; } }
        long common = Math.Min(left.Length, right.Length);
        for (long i = 0; i < common; i++)
        {
            if (left[i] == right[i]) continue;
            if (start >= 0 && i - end > gap) Flush(end - start + 1);
            if (start < 0) start = i;
            end = i;
            if (segments.Count >= 4096) { start = start >= 0 ? start : i; Flush(Math.Max(left.Length, right.Length) - start); return segments; }
        }
        if (common < Math.Max(left.Length, right.Length)) { if (start >= 0 && common - end > gap) Flush(end - start + 1); start = start >= 0 ? start : common; end = Math.Max(left.Length, right.Length) - 1; }
        Flush(end - start + 1);
        return segments;
    }

    /// <summary>Reads the pixel dimensions straight from the file headers; no imaging dependency.</summary>
    public static (string Format, (int Width, int Height)? Size) Probe(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 24 && data[0] == 0x89 && data[1] == 'P')
            return ("PNG", (BinaryPrimitives.ReadInt32BigEndian(data[16..20]), BinaryPrimitives.ReadInt32BigEndian(data[20..24])));
        if (data.Length >= 10 && data[0] == 'G' && data[1] == 'I')
            return ("GIF", (BinaryPrimitives.ReadUInt16LittleEndian(data[6..8]), BinaryPrimitives.ReadUInt16LittleEndian(data[8..10])));
        if (data.Length >= 26 && data[0] == 'B' && data[1] == 'M')
        {
            int header = BinaryPrimitives.ReadInt32LittleEndian(data[14..18]);
            return header == 12
                ? ("BMP", (BinaryPrimitives.ReadUInt16LittleEndian(data[18..20]), BinaryPrimitives.ReadUInt16LittleEndian(data[20..22])))
                : ("BMP", (BinaryPrimitives.ReadInt32LittleEndian(data[18..22]), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data[22..26]))));
        }
        if (data.Length > 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            int i = 2;
            while (i + 9 < data.Length)
            {
                if (data[i] != 0xFF) { i++; continue; }
                byte marker = data[i + 1];
                if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
                    return ("JPEG", (BinaryPrimitives.ReadUInt16BigEndian(data[(i + 7)..(i + 9)]), BinaryPrimitives.ReadUInt16BigEndian(data[(i + 5)..(i + 7)])));
                if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }
                i += 2 + BinaryPrimitives.ReadUInt16BigEndian(data[(i + 2)..(i + 4)]);
            }
            return ("JPEG", null);
        }
        return (Path.GetExtension("?").TrimStart('.').ToUpperInvariant(), null);
    }
}
