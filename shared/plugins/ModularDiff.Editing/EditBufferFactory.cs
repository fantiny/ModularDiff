using ModularDiff.Contracts;

namespace ModularDiff.Editing;

public sealed class EditBufferFactory : IEditBufferFactory
{
    public string Id => "edit-buffer";
    public string DisplayName => "Transactional edit buffer";
    public IEditBuffer Create(DocumentSnapshot snapshot) => new EditBuffer(snapshot);
}

internal sealed class EditBuffer(DocumentSnapshot original) : IEditBuffer
{
    private sealed record Change(int Offset, string Removed, string Inserted, string Label);
    private readonly List<Change> _undo = [], _redo = [];
    private string _savedText = original.Text;
    private DiskStamp _disk = original.Disk;
    private long _lastTyping;
    public string Path => original.Path;
    public string Text { get; private set; } = original.Text;
    public long Version { get; private set; }
    public string NewLine { get; } = DocumentLines.Parse(original.Text).FirstOrDefault(l => l.Ending.Length > 0)?.Ending ?? Environment.NewLine;
    public bool IsDirty => !string.Equals(Text, _savedText, StringComparison.Ordinal);
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public BufferSnapshot Snapshot() => new(Path, Text, original.Encoding, _disk, Version);
    public int Apply(TextEdit edit)
    {
        if (edit.Offset < 0 || edit.Length < 0 || edit.Offset > Text.Length - edit.Length) throw new ArgumentOutOfRangeException(nameof(edit));
        if ((long)Text.Length - edit.Length + edit.Text.Length > 8 * 1024 * 1024) throw new InvalidOperationException("Editable text exceeds the 8M-character limit.");
        string removed = Text.Substring(edit.Offset, edit.Length);
        if (removed == edit.Text) return edit.Offset + edit.Text.Length;
        var change = new Change(edit.Offset, removed, edit.Text, edit.Description);
        long now = Environment.TickCount64;
        // Contiguous typing is one undo step; merges/paste/delete always form their own transaction.
        if (edit.Description == "Typing" && removed.Length == 0 && !edit.Text.Contains('\n') && !edit.Text.Contains('\r')
            && _undo.LastOrDefault() is { Label: "Typing", Removed.Length: 0 } previous
            && previous.Offset + previous.Inserted.Length == edit.Offset && now - _lastTyping < 750 && _redo.Count == 0)
            _undo[^1] = previous with { Inserted = previous.Inserted + edit.Text };
        else _undo.Add(change);
        _lastTyping = edit.Description == "Typing" ? now : 0;
        _redo.Clear();
        Text = Text.Remove(edit.Offset, edit.Length).Insert(edit.Offset, edit.Text); Version++;
        long retained = _undo.Sum(c => (long)c.Removed.Length + c.Inserted.Length);
        while (_undo.Count > 1 && (_undo.Count > 256 || retained > 16 * 1024 * 1024))
        { retained -= _undo[0].Removed.Length + _undo[0].Inserted.Length; _undo.RemoveAt(0); }
        return edit.Offset + edit.Text.Length;
    }
    public int Undo()
    {
        if (!CanUndo) return 0;
        var change = _undo[^1]; _undo.RemoveAt(_undo.Count - 1); _redo.Add(change); _lastTyping = 0;
        Text = Text.Remove(change.Offset, change.Inserted.Length).Insert(change.Offset, change.Removed); Version++;
        return change.Offset + change.Removed.Length;
    }
    public int Redo()
    {
        if (!CanRedo) return 0;
        var change = _redo[^1]; _redo.RemoveAt(_redo.Count - 1); _undo.Add(change); _lastTyping = 0;
        Text = Text.Remove(change.Offset, change.Removed.Length).Insert(change.Offset, change.Inserted); Version++;
        return change.Offset + change.Inserted.Length;
    }
    public void AcceptSave(BufferSnapshot saved, DiskStamp disk)
    {
        if (saved.Path != Path || saved.Version > Version) throw new InvalidOperationException("Save snapshot does not belong to this buffer.");
        _savedText = saved.Text; _disk = disk; _lastTyping = 0;
        // History is retained: undo after saving makes the buffer dirty again.
    }
}
