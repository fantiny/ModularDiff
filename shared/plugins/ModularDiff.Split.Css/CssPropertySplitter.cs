using System.Text;
using ModularDiff.Contracts;

namespace ModularDiff.Split.Css;

/// <summary>把一条包含多个声明的 CSS 行拆成规范形式:选择器块开头独占一行,每条声明一行,收尾 '}' 另起一行;
/// 顶层分号处断开,保留原行缩进,引号与括号内不参与切分(url(data:...;…) 保持完整),
/// 非声明式内容(含 =、::)不拆。</summary>
public sealed class CssPropertySplitter : ILineSplitter
{
    public string Id => "css-split";
    public string DisplayName => "CSS 属性换行";

    public bool CanSplit(string fileName, string line) => Split(line).Count > 1;

    public IReadOnlyList<string> Split(string line)
    {
        string trimmed = line.TrimStart();
        string indent = line[..(line.Length - trimmed.Length)];
        // 选择器与声明同行(body{margin:0;…):先把块开头(连同原空格风格)拆成独立行。
        string opener = "";
        int open = trimmed.IndexOf('{');
        if (open >= 0)
        {
            string head = trimmed[..open].TrimEnd();
            if (head.Length > 0 && !head.Contains(';') && !head.Contains('}') && !head.Contains('"') && !head.Contains('\''))
            {
                opener = trimmed[..(open + 1)];
                trimmed = trimmed[(open + 1)..].TrimStart();
            }
        }
        var segments = new List<string>();
        var current = new StringBuilder();
        int depth = 0;
        char quote = '\0';
        foreach (char c in trimmed)
        {
            if (quote != '\0') { current.Append(c); if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') { quote = c; current.Append(c); continue; }
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth = Math.Max(0, depth - 1);
            if (c == ';' && depth == 0)
            {
                var segment = current.ToString().Trim();
                if (segment.Length > 0) segments.Add(segment + ";");
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        var tail = current.ToString().Trim();
        if (tail.Length > 0) segments.Add(tail);
        // 末段收尾 '}' 另起一行(仅在已经确定要拆分的形态下)。
        if (segments.Count > 0 && segments[^1].Length > 1 && segments[^1].EndsWith('}'))
        {
            string last = segments[^1];
            segments[^1] = last[..^1].TrimEnd();
            if (segments[^1].Length > 0 && !segments[^1].EndsWith(';')) segments[^1] += ";";
            segments.Add("}");
        }
        bool declarative = (opener.Length > 0 || segments.Count >= 2)
            && segments.Where(s => s != "}").All(s => s.Length > 0 && s.Contains(':') && !s.Contains('{') && !s.Contains('}') && !s.Contains('=') && !s.Contains("::"));
        if (!declarative) return [line];
        var result = new List<string>();
        if (opener.Length > 0) result.Add(indent + opener);
        result.AddRange(segments.Select(s => indent + s));
        return result;
    }
}
