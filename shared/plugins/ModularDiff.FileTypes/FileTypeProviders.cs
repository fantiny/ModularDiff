using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ModularDiff.Contracts;

namespace ModularDiff.FileTypes;

// ─── JSON ───

public sealed class JsonFileType : IFileTypeProvider
{
    public string Id => "filetype-json";
    public string DisplayName => "JSON tools";
    public IReadOnlyList<string> Extensions => [".json", ".jsonc"];
    public string LanguageName => "JSON";

    public IReadOnlyList<(string Label, string? Shortcut, Func<EditContext, string> Transform)> EditActions =>
    [
        ("格式化 JSON（缩进 2）", null, ctx => Pretty(ctx.Buffer.Text, 2)),
        ("格式化 JSON（缩进 4）", null, ctx => Pretty(ctx.Buffer.Text, 4)),
        ("压缩 JSON（单行）", null, ctx => Compact(ctx.Buffer.Text)),
        ("排序 JSON 键", null, ctx => SortKeys(ctx.Buffer.Text)),
    ];

    private static string Pretty(string text, int indent)
    {
        try { return JsonNode.Parse(text)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true, IndentSize = indent }) ?? text; }
        catch { return text; }
    }
    private static string Compact(string text)
    {
        try { return JsonNode.Parse(text)?.ToJsonString() ?? text; }
        catch { return text; }
    }
    private static string SortKeys(string text)
    {
        try
        {
            var node = JsonNode.Parse(text);
            return SortNode(node)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? text;
        }
        catch { return text; }
    }
    private static JsonNode? SortNode(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var sorted = new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal));
            foreach (var (key, value) in sorted) sorted[key] = SortNode(value);
            return sorted;
        }
        if (node is JsonArray array)
        {
            var sorted = new JsonArray();
            foreach (var item in array) sorted.Add(SortNode(item));
            return sorted;
        }
        return node;
    }
}

// ─── C# / C-family languages ───

public sealed class CSharpFileType : IFileTypeProvider
{
    public string Id => "filetype-csharp";
    public string DisplayName => "C# / C-family tools";
    public IReadOnlyList<string> Extensions => [".cs", ".c", ".h", ".cpp", ".hpp", ".java", ".ts", ".tsx", ".js", ".jsx", ".swift", ".kt"];
    public string LanguageName => "C#";

    public IReadOnlyList<(string Label, string? Shortcut, Func<EditContext, string> Transform)> EditActions =>
    [
        ("注释选中行 (//)", "Ctrl+/", ctx => CommentLines(ctx.Buffer.Text, "//")),
        ("取消注释", "Ctrl+Shift+/", ctx => UncommentLines(ctx.Buffer.Text)),
        ("修剪行尾空白", null, ctx => string.Join("\n", ctx.Buffer.Text.Split('\n').Select(l => l.TrimEnd(' ', '\t', '\r')))),
    ];

    private static string CommentLines(string text, string prefix)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Trim().Length > 0 && !lines[i].TrimStart().StartsWith(prefix))
                lines[i] = prefix + lines[i];
        return string.Join("\n", lines);
    }
    private static string UncommentLines(string text)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("//"))
                lines[i] = lines[i].Remove(lines[i].IndexOf("//"), 2);
        }
        return string.Join("\n", lines);
    }
}

// ─── XML ───

public sealed class XmlFileType : IFileTypeProvider
{
    public string Id => "filetype-xml";
    public string DisplayName => "XML tools";
    public IReadOnlyList<string> Extensions => [".xml", ".xaml", ".config", ".csproj", ".props", ".targets"];
    public string LanguageName => "XML";

    public IReadOnlyList<(string Label, string? Shortcut, Func<EditContext, string> Transform)> EditActions =>
    [
        ("格式化 XML（缩进 2）", null, ctx => Format(ctx.Buffer.Text)),
        ("压缩 XML（单行）", null, ctx => Compact(ctx.Buffer.Text)),
        ("转义 XML 特殊字符", null, ctx => ctx.Buffer.Text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")),
    ];

    private static string Format(string text)
    {
        try { return XDocument.Parse(text).ToString(SaveOptions.None); }
        catch { return text; }
    }
    private static string Compact(string text)
    {
        try { return XDocument.Parse(text).ToString(SaveOptions.DisableFormatting); }
        catch { return text; }
    }
}

// ─── YAML ───

public sealed class YamlFileType : IFileTypeProvider
{
    public string Id => "filetype-yaml";
    public string DisplayName => "YAML tools";
    public IReadOnlyList<string> Extensions => [".yaml", ".yml"];
    public string LanguageName => "YAML";

    public IReadOnlyList<(string Label, string? Shortcut, Func<EditContext, string> Transform)> EditActions =>
    [
        ("Tab → 空格（2 格）", null, ctx => ctx.Buffer.Text.Replace("\t", "  ")),
        ("修剪行尾空白", null, ctx => string.Join("\n", ctx.Buffer.Text.Split('\n').Select(l => l.TrimEnd(' ', '\t', '\r')))),
    ];
}

// ─── Markdown ───

public sealed class MarkdownFileType : IFileTypeProvider
{
    public string Id => "filetype-markdown";
    public string DisplayName => "Markdown tools";
    public IReadOnlyList<string> Extensions => [".md", ".markdown"];
    public string LanguageName => "Markdown";

    public IReadOnlyList<(string Label, string? Shortcut, Func<EditContext, string> Transform)> EditActions =>
    [
        ("生成目录 (TOC)", null, ctx => GenerateToc(ctx.Buffer.Text)),
    ];

    private static string GenerateToc(string text)
    {
        var headings = text.Split('\n')
            .Where(l => l.StartsWith("#"))
            .Select(l => (Level: l.TakeWhile(c => c == '#').Count(), Title: l.TrimStart('#', ' ')));
        var toc = new List<string>();
        foreach (var (level, title) in headings)
            toc.Add(new string(' ', (level - 1) * 2) + "- [" + title + "](#" + title.ToLowerInvariant().Replace(' ', '-') + ")");
        return string.Join("\n", toc) + "\n\n" + text;
    }
}

// ─── Shell / Script ───

public sealed class ScriptFileType : IFileTypeProvider
{
    public string Id => "filetype-script";
    public string DisplayName => "Script tools";
    public IReadOnlyList<string> Extensions => [".sh", ".bash", ".zsh", ".ps1", ".psm1", ".bat", ".cmd", ".py", ".rb"];
    public string LanguageName => "Script";

    public IReadOnlyList<(string Label, string? Shortcut, Func<EditContext, string> Transform)> EditActions =>
    [
        ("注释选中行 (#)", null, ctx => Comment(ctx.Buffer.Text)),
        ("取消注释", null, ctx => Uncomment(ctx.Buffer.Text)),
        ("CRLF → LF", null, ctx => ctx.Buffer.Text.Replace("\r\n", "\n")),
        ("LF → CRLF", null, ctx => ctx.Buffer.Text.Replace("\r\n", "\n").Replace("\n", "\r\n")),
    ];

    private static string Comment(string text)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Trim().Length > 0 && !lines[i].TrimStart().StartsWith("#"))
                lines[i] = "# " + lines[i];
        return string.Join("\n", lines);
    }
    private static string Uncomment(string text)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("# "))
                lines[i] = lines[i].Remove(lines[i].IndexOf("# "), 2);
            else if (trimmed.StartsWith("#"))
                lines[i] = lines[i].Remove(lines[i].IndexOf('#'), 1);
        }
        return string.Join("\n", lines);
    }
}
