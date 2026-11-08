using System.Security.Cryptography;
using System.Text;
using ModularDiff.Contracts;

namespace ModularDiff.Editing;

public sealed class TextStorage : ITextStorage
{
    public string Id => "text-storage";
    public string DisplayName => "Encoding-preserving atomic storage";
    public async Task<DocumentSnapshot> LoadAsync(string path, bool allowMissing = false, CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path); cancellationToken.ThrowIfCancellationRequested();
        if (allowMissing && !File.Exists(path) && !Directory.Exists(path)) return new(path, "", new(), new(false, ""));
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (stream.Length > 8 * 1024 * 1024) throw new InvalidOperationException("Files over 8 MiB remain read-only.");
        var bytes = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(bytes, cancellationToken);
        (string name, int skip) = bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }) ? ("utf-32", 4)
            : bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff }) ? ("utf-32BE", 4)
            : bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }) ? ("utf-16", 2)
            : bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }) ? ("utf-16BE", 2)
            : bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? ("utf-8", 3) : ("utf-8", 0);
        var info = new TextEncodingInfo(name, skip > 0);
        string text = EncodingFor(info).GetString(bytes, skip, bytes.Length - skip);
        if (text.Contains('\0')) throw new InvalidOperationException("Binary files cannot be edited as text.");
        return new(path, text, info, new(true, Convert.ToHexString(SHA256.HashData(bytes))));
    }
    public async Task<DiskStamp> SaveAsync(BufferSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        string path = Path.GetFullPath(snapshot.Path);
        var before = await Stamp(path, cancellationToken);
        if (before != snapshot.Disk) throw new DiskConflictException("磁盘文件已被其他程序修改或替换，未覆盖文件；当前编辑仍保留。");
        if (before.Exists && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) throw new UnauthorizedAccessException("文件为只读，无法保存。");
        var encoding = EncodingFor(snapshot.Encoding);
        byte[] body = encoding.GetBytes(snapshot.Text);
        byte[] preamble = snapshot.Encoding.HasBom ? encoding.GetPreamble() : [];
        byte[] bytes = [.. preamble, .. body];
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".modulardiff-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await stream.WriteAsync(bytes, cancellationToken); await stream.FlushAsync(cancellationToken); stream.Flush(true); }
            cancellationToken.ThrowIfCancellationRequested();
            if (await Stamp(path, cancellationToken) != before) throw new DiskConflictException("保存期间磁盘文件发生变化，未覆盖文件。");
            if (before.Exists) File.Replace(temporary, path, null);
            else File.Move(temporary, path); // No overwrite: also protects a newly created competing file.
            return new(true, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static async Task<DiskStamp> Stamp(string path, CancellationToken token)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            return new(true, Convert.ToHexString(await SHA256.HashDataAsync(stream, token)));
        }
        catch (FileNotFoundException) { return new(false, ""); }
        catch (DirectoryNotFoundException) { return new(false, ""); }
    }
    private static Encoding EncodingFor(TextEncodingInfo info) => info.Name switch
    {
        "utf-8" => new UTF8Encoding(info.HasBom, true), "utf-16" => new UnicodeEncoding(false, info.HasBom, true),
        "utf-16BE" => new UnicodeEncoding(true, info.HasBom, true), "utf-32" => new UTF32Encoding(false, info.HasBom, true),
        "utf-32BE" => new UTF32Encoding(true, info.HasBom, true), _ => throw new NotSupportedException("Unsupported encoding: " + info.Name)
    };
}
