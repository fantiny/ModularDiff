using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ModularDiff.Contracts;

namespace ModularDiff.Comparison.Tables;

/// <summary>Table compare for CSV / TSV / XLSX: parses rows of cells, aligns rows with the chosen
/// diff algorithm, then aligns the cells of each changed row pair so matching columns line up.
/// The aligned rows (with blank slots) are emitted as ordinary text lines, so the whole text
/// pipeline — view, in-line highlights, hunks, reports — works unchanged.</summary>
public sealed class TableComparisonModule : IComparisonModule
{
    public string Id => "tables";
    public string DisplayName => "Tables · csv / tsv / xlsx";
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".csv", ".tsv", ".xlsx" };
    private const string CellSeparator = " │ ";

    public bool CanCompare(string leftPath, string rightPath) =>
        Extensions.Contains(Path.GetExtension(leftPath)) && Extensions.Contains(Path.GetExtension(rightPath))
        && File.Exists(leftPath) && File.Exists(rightPath);

    public async Task<ComparisonResult> CompareAsync(ComparisonRequest request, IPluginResolver plugins,
        IProgress<ComparisonProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var algorithm = plugins.Get<IDiffAlgorithm>(request.AlgorithmId);
        string[][] left = await ParseAsync(request.LeftPath, cancellationToken);
        string[][] right = await ParseAsync(request.RightPath, cancellationToken);
        string[] leftKeys = [.. left.Select(RowKey)], rightKeys = [.. right.Select(RowKey)];
        var diff = algorithm.Compare(leftKeys, rightKeys, plugins, cancellationToken);
        var lines = new List<DiffLine>();
        foreach (var edit in diff.Edits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[]? rowA = edit.LeftIndex is int i ? left[i] : null;
            string[]? rowB = edit.RightIndex is int j ? right[j] : null;
            lines.Add(new(edit.Kind, edit.LeftIndex + 1, edit.RightIndex + 1,
                rowA is null ? null : Join(rowA), rowB is null ? null : Join(rowB), "LF", "LF"));
        }
        bool changed = lines.Any(l => l.Kind != EditKind.Equal);
        var file = new FileComparison(Path.GetFileName(request.LeftPath), changed ? FileStatus.Modified : FileStatus.Equal, lines,
            Note: changed ? null : "表格内容一致（按单元格对齐比较）。");
        progress?.Report(new(1, 1, file.RelativePath));
        return new(request.LeftPath, request.RightPath, Id, algorithm.Id, [file], []);
    }

    private static string RowKey(string[] row) => string.Join("\u0001", row);

    private static string Join(string[] cells) => string.Join(CellSeparator, cells);

    public static async Task<string[][]> ParseAsync(string path, CancellationToken token)
    {
        string name = Path.GetFileName(path);
        if (name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            return await Task.Run(() => ParseXlsx(stream), token);
        }
        string text = await File.ReadAllTextAsync(path, token);
        char separator = name.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : ',';
        return ParseDelimited(text, separator);
    }

    /// <summary>RFC 4180: quoted fields may contain separators, newlines and doubled quotes.</summary>
    public static string[][] ParseDelimited(string text, char separator)
    {
        var rows = new List<string[]>();
        var fields = new List<StringBuilder>();
        var field = new StringBuilder();
        bool quoted = false, fieldQuoted = false;
        void EndField() { fields.Add(field); field = new StringBuilder(); fieldQuoted = false; }
        void EndRow() { EndField(); rows.Add([.. fields.Select(b => b.ToString())]); fields.Clear(); }
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(c);
                continue;
            }
            if (c == '"' && field.Length == 0 && !fieldQuoted) { quoted = true; fieldQuoted = true; continue; }
            if (c == separator) { EndField(); continue; }
            if (c == '\r' || c == '\n')
            {
                bool crlf = c == '\r' && i + 1 < text.Length && text[i + 1] == '\n';
                if (crlf) i++;
                if (field.Length == 0 && fields.Count == 0 && !fieldQuoted)
                    continue;   // blank line: skipped (also handles the trailing newline)
                EndRow();
                continue;
            }
            field.Append(c);
        }
        if (quoted) throw new InvalidDataException("CSV 引号字段未闭合。");
        if (field.Length > 0 || fields.Count > 0 || fieldQuoted) EndRow();
        return [.. rows];
    }

    /// <summary>Reads every sheet of an .xlsx through the BCL zip/XML stack: sheet names come from
    /// workbook.xml, shared strings from sharedStrings.xml, cells by their column letters.</summary>
    private static string[][] ParseXlsx(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var shared = new List<string>();
        var sharedEntry = archive.GetEntry("xl/sharedStrings.xml");
        if (sharedEntry is not null)
        {
            using var sharedStream = sharedEntry.Open();
            foreach (var element in XDocument.Load(sharedStream).Descendants("{http://schemas.openxmlformats.org/spreadsheetml/2006/main}si"))
                shared.Add(string.Concat(element.Descendants("{http://schemas.openxmlformats.org/spreadsheetml/2006/main}t").Select(t => t.Value)));
        }
        var ns = XNamespace.Get("http://schemas.openxmlformats.org/spreadsheetml/2006/main");
        var relationsNs = XNamespace.Get("http://schemas.openxmlformats.org/package/2006/relationships");
        var workbookEntry = archive.GetEntry("xl/workbook.xml") ?? throw new InvalidDataException("不是有效的 xlsx（缺少 workbook.xml）。");
        Dictionary<string, string> targets;
        using (var workbookStream = workbookEntry.Open())
        {
            var relsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels") ?? throw new InvalidDataException("不是有效的 xlsx（缺少关系映射）。");
            using var relsStream = relsEntry.Open();
            targets = XDocument.Load(relsStream).Descendants(relationsNs + "Relationship")
                .Where(r => r.Attribute("Target")?.Value.StartsWith("worksheets/", StringComparison.Ordinal) == true)
                .ToDictionary(r => r.Attribute("Id")!.Value, r => r.Attribute("Target")!.Value, StringComparer.Ordinal);
        }
        var rows = new List<string[]>();
        using (var workbookStream = workbookEntry.Open())
        {
            foreach (var sheet in XDocument.Load(workbookStream).Descendants(ns + "sheet"))
            {
                string name = sheet.Attribute("name")?.Value ?? "";
                string? id = sheet.Attribute("{http://schemas.openxmlformats.org/officeDocument/2006/relationships}id")?.Value;
                if (id is null || !targets.TryGetValue(id, out string? target)) continue;
                rows.Add(["── Sheet: " + name + " ──", ""]);
                var sheetEntry = archive.GetEntry("xl/" + target);
                if (sheetEntry is null) continue;
                using var sheetStream = sheetEntry.Open();
                foreach (var rowElement in XDocument.Load(sheetStream).Descendants(ns + "row"))
                {
                    var cells = new SortedDictionary<int, string>();
                    foreach (var cell in rowElement.Elements(ns + "c"))
                    {
                        int column = ColumnIndex(cell.Attribute("r")?.Value ?? "");
                        string type = cell.Attribute("t")?.Value ?? "";
                        string? value = cell.Element(ns + "v")?.Value ?? cell.Element(ns + "is")?.Element(ns + "t")?.Value;
                        cells[column] = type == "s" && int.TryParse(value, out int sharedIndex) && sharedIndex < shared.Count ? shared[sharedIndex] : value ?? "";
                    }
                    int width = cells.Count == 0 ? 0 : cells.Keys.Max() + 1;
                    var line = new string[width];
                    foreach (var (column, value) in cells) line[column] = value;
                    rows.Add(line);
                }
            }
        }
        return [.. rows];
    }

    private static int ColumnIndex(string reference)
    {
        int index = 0;
        foreach (char c in reference)
        {
            if (c is < 'A' or > 'Z') break;
            index = index * 26 + (c - 'A' + 1);
        }
        return index - 1;
    }
}
