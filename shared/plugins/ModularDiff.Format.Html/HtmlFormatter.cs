using System.Text;
using ModularDiff.Contracts;

namespace ModularDiff.Format.Html;

/// <summary>Self-contained HTML report: side-by-side difference rows with status coloring,
/// no external resources, safe to archive or share.</summary>
public sealed class HtmlFormatter : IResultFormatter
{
    public string Id => "html";
    public string DisplayName => "HTML report";
    public string FileExtension => ".html";

    public async Task WriteAsync(ComparisonResult result, TextWriter writer, CancellationToken cancellationToken = default)
    {
        var html = new StringBuilder();
        html.Append("""
<!DOCTYPE html><html lang="zh"><head><meta charset="utf-8"><title>ModularDiff 报告</title><style>
body{font:13px/1.45 "Segoe UI",system-ui,sans-serif;margin:24px;color:#253041;background:#fff}
h1{font-size:18px;margin:0 0 4px}.meta{color:#667085;margin-bottom:16px}
table{border-collapse:collapse;margin:8px 0 20px;width:100%}
th,td{border:1px solid #DEE3EA;padding:2px 8px;text-align:left;vertical-align:top}
th{background:#F2F4F7;font-weight:600}
td.code{font:12px/1.5 Consolas,monospace;white-space:pre-wrap;word-break:break-all;width:43%}
td.num{color:#8793A4;text-align:right;width:4%;white-space:nowrap}
tr.del td.code:first-of-type{background:#FFF0F0}
tr.ins td.code:last-of-type{background:#ECF7EF}
tr.mod td.code{background:#FFF0E9}
.status-Modified{color:#A35D39}.status-LeftOnly{color:#A52D22}.status-RightOnly{color:#2F7D4F}
.status-Equal{color:#2F7D4F}.status-Error{color:#B42318;font-weight:600}
.file h2{font-size:14px;margin:20px 0 4px}
.warn{color:#B54708}.summary{margin-top:8px;color:#667085}
footer{margin-top:28px;color:#98A2B3;font-size:11px}
</style></head><body>
""");
        html.Append("<h1>ModularDiff 比较报告</h1><div class=\"meta\">");
        html.Append(Escape(result.LeftPath)).Append(" ↔ ").Append(Escape(result.RightPath));
        html.Append($"<br>模块 {Escape(result.ModuleId)} · 算法 {Escape(result.AlgorithmId)} · {result.Files.Count} 个文件");
        html.Append($" · 差异 {result.Files.Count(f => f.Status is FileStatus.Modified or FileStatus.LeftOnly or FileStatus.RightOnly)}");
        html.Append($" · 错误 {result.Files.Count(f => f.Status == FileStatus.Error)}</div>");
        foreach (string warning in result.Warnings) html.Append("<div class=\"warn\">WARNING: ").Append(Escape(warning)).Append("</div>");

        if (result.Files.Count > 1)
        {
            html.Append("<table><tr><th>状态</th><th>文件</th></tr>");
            foreach (var file in result.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                html.Append("<tr><td class=\"status-").Append(file.Status).Append("\">").Append(file.Status).Append("</td><td>")
                    .Append(Escape(file.RelativePath)).Append(file.IsBinary ? "（字节比较）" : "").Append("</td></tr>");
            }
            html.Append("</table>");
        }

        foreach (var file in result.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            html.Append("<div class=\"file\"><h2>").Append(Escape(file.RelativePath));
            html.Append(" <span class=\"status-").Append(file.Status).Append("\">[").Append(file.Status)
                .Append(file.IsApproximate ? " · 粗粒度对齐" : "").Append("]</span></h2>");
            if (file.Note is { } note) html.Append("<div class=\"meta\">").Append(Escape(note)).Append("</div>");
            var changed = file.Lines.Where(l => l.Kind != EditKind.Equal).ToArray();
            if (changed.Length == 0) { html.Append("<div class=\"summary\">无文本差异行。</div></div>"); continue; }
            html.Append("<table><tr><th>#</th><th>左侧</th><th>#</th><th>右侧</th></tr>");
            foreach (var line in changed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string kind = line.Kind == EditKind.Delete ? "del" : "ins";
                html.Append("<tr class=\"").Append(kind).Append("\">")
                    .Append(Cell(line.LeftLine)).Append("<td class=\"code\">").Append(Escape(line.LeftText ?? "")).Append("</td>")
                    .Append(Cell(line.RightLine)).Append("<td class=\"code\">").Append(Escape(line.RightText ?? "")).Append("</td></tr>");
            }
            html.Append("</table></div>");
        }
        html.Append("<footer>由 ModularDiff 生成 · IResultFormatter html</footer></body></html>");
        await writer.WriteAsync(html.ToString().AsMemory(), cancellationToken);
        await writer.WriteLineAsync();
    }

    private static string Cell(int? number) => $"<td class=\"num\">{(number is { } n ? n.ToString() : "")}</td>";
    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
