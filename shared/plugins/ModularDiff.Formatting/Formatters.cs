using System.Text.Json;
using System.Text.Json.Serialization;
using ModularDiff.Contracts;

namespace ModularDiff.Formatting;

public sealed class JsonFormatter : IResultFormatter
{
    public string Id => "json";
    public string DisplayName => "JSON report";
    public string FileExtension => ".json";
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    };
    public async Task WriteAsync(ComparisonResult result, TextWriter writer, CancellationToken cancellationToken = default)
    {
        await writer.WriteAsync(JsonSerializer.Serialize(result, Options).AsMemory(), cancellationToken);
        await writer.WriteLineAsync();
    }
}

public sealed class TextFormatter : IResultFormatter
{
    public string Id => "text";
    public string DisplayName => "Plain text report";
    public string FileExtension => ".txt";
    public async Task WriteAsync(ComparisonResult result, TextWriter writer, CancellationToken cancellationToken = default)
    {
        await writer.WriteLineAsync($"Left:  {result.LeftPath}\nRight: {result.RightPath}\nModule: {result.ModuleId} | Algorithm: {result.AlgorithmId}");
        foreach (var warning in result.Warnings) await writer.WriteLineAsync($"WARNING: {warning}");
        // A folder that exists on one side only is a finding of its own; folders present on both
        // sides are structure (their files are listed one by one) and are not reported as rows.
        foreach (var file in result.Files.Where(Reported))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync($"\n[{file.Status}] {file.RelativePath}{(file.IsDirectory ? "/" : "")}{(file.IsBinary ? " (byte comparison)" : "")}");
            if (file.Note is not null) await writer.WriteLineAsync($"  {file.Note}");
            foreach (var line in file.Lines.Where(l => l.Kind != EditKind.Equal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var marker = line.Kind == EditKind.Delete ? "-" : "+";
                var number = line.Kind == EditKind.Delete ? line.LeftLine : line.RightLine;
                var text = line.Kind == EditKind.Delete ? line.LeftText : line.RightText;
                var ending = line.Kind == EditKind.Delete ? line.LeftEnding : line.RightEnding;
                await writer.WriteLineAsync($"{marker} {number,6} | {text} [{ending}]");
            }
        }
        var files = result.Files.Where(f => !f.IsDirectory).ToArray();   // folder rows are never counted as files
        int oneSideFolders = result.Files.Count(f => f.IsDirectory && f.Status is FileStatus.LeftOnly or FileStatus.RightOnly);
        // The exit code treats a folder that exists on one side only as a difference, so a summary
        // that said "changed: 0" next to such a row would contradict it.
        await writer.WriteLineAsync($"\nFiles: {files.Length}; changed: {files.Count(f => f.Status is FileStatus.Modified or FileStatus.LeftOnly or FileStatus.RightOnly)}; errors: {files.Count(f => f.Status == FileStatus.Error)}{(oneSideFolders > 0 ? $"; one-side folders: {oneSideFolders}" : "")}");
    }

    /// <summary>Rows worth a line in a report: every file, plus folders that exist on one side only.</summary>
    internal static bool Reported(FileComparison entry) =>
        !entry.IsDirectory || entry.Status is FileStatus.LeftOnly or FileStatus.RightOnly;
}
