namespace ModularDiff.Contracts;

public sealed record DiskStamp(bool Exists, string Hash);
public sealed record TextEncodingInfo(string Name = "utf-8", bool HasBom = false);
public sealed record DocumentSnapshot(string Path, string Text, TextEncodingInfo Encoding, DiskStamp Disk);
public sealed record BufferSnapshot(string Path, string Text, TextEncodingInfo Encoding, DiskStamp Disk, long Version);
public sealed record TextEdit(int Offset, int Length, string Text, string Description = "Edit");
public interface IEditBuffer
{
    string Path { get; }
    string Text { get; }
    string NewLine { get; }
    long Version { get; }
    bool IsDirty { get; }
    bool CanUndo { get; }
    bool CanRedo { get; }
    BufferSnapshot Snapshot();
    int Apply(TextEdit edit);
    int Undo();
    int Redo();
    void AcceptSave(BufferSnapshot saved, DiskStamp disk);
}
public interface IEditBufferFactory : IPlugin { IEditBuffer Create(DocumentSnapshot snapshot); }
public interface ITextStorage : IPlugin
{
    Task<DocumentSnapshot> LoadAsync(string path, bool allowMissing = false, CancellationToken cancellationToken = default);
    Task<DiskStamp> SaveAsync(BufferSnapshot snapshot, CancellationToken cancellationToken = default);
}
public sealed class DiskConflictException(string message) : IOException(message);
public sealed record BufferComparison(long LeftVersion, long RightVersion, FileComparison File);
public interface IBufferComparison : IPlugin
{
    BufferComparison Compare(BufferSnapshot left, BufferSnapshot right, string algorithmId, ComparisonOptions options,
        IPluginResolver plugins, CancellationToken cancellationToken = default);
}
public enum MergeDirection { LeftToRight, RightToLeft }
public interface IBlockMerge : IPlugin
{
    TextEdit Plan(BufferComparison comparison, BufferSnapshot left, BufferSnapshot right, int hunkId, MergeDirection direction);
}

public sealed record DocumentLine(int Offset, string Text, string Ending)
{
    public int Length => Text.Length + Ending.Length;
    public string EndingLabel => Ending switch { "\r\n" => "CRLF", "\n" => "LF", "\r" => "CR", _ => "EOF" };
}
public static class DocumentLines
{
    // No artificial last line: the editor handles the caret position after a trailing newline separately.
    public static IReadOnlyList<DocumentLine> Parse(string text)
    {
        var lines = new List<DocumentLine>(); int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n')) continue;
            int end = i;
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            lines.Add(new(start, text[start..end], text[end..(i + 1)])); start = i + 1;
        }
        if (start < text.Length) lines.Add(new(start, text[start..], ""));
        return lines;
    }
}
