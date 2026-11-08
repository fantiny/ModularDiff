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
        foreach (var file in result.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync($"\n[{file.Status}] {file.RelativePath}{(file.IsBinary ? " (byte comparison)" : "")}");
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
        await writer.WriteLineAsync($"\nFiles: {result.Files.Count}; changed: {result.Files.Count(f => f.Status is FileStatus.Modified or FileStatus.LeftOnly or FileStatus.RightOnly)}; errors: {result.Files.Count(f => f.Status == FileStatus.Error)}");
    }
}
