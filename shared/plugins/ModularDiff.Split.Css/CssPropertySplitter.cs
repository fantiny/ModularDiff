using System.Text;
using ModularDiff.Contracts;

namespace ModularDiff.Split.Css;

/// <summary>把一条包含多个声明的 CSS 行拆成"每声明一行":在顶层分号处断开,保留原行缩进;
/// 引号与括号内的分号不参与切分(url(data:...;...) 等保持完整),非声明式内容(含 =、::、花括号)不拆。</summary>
public sealed class CssPropertySplitter : ILineSplitter
{
    public string Id => "css-split";
    public string DisplayName => "CSS 属性换行";

    public bool CanSplit(string fileName, string line) => Split(line).Count > 1;

    public IReadOnlyList<string> Split(string line)
    {
        string trimmed = line.TrimStart();
        string indent = line[..(line.Length - trimmed.Length)];
        var segments = new List<string>();
        var current = new StringBuilder();
        int depth = 0;
        char quote = '\0';
        foreach (char c in trimmed)
        {
            if (quote != '\0') { current.Append(c); if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') { quote = c; current.Append(c); continue; }
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']' && depth > 0) depth--;
            if (c == ';' && depth == 0)
            {
                var segment = current.ToString().TrimEnd();
                if (segment.Length > 0) segments.Add(segment + ";");
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        var tail = current.ToString().TrimEnd();
        if (tail.Length > 0) segments.Add(tail);
        // 只对"声明式"内容生效:除末段外每段都应是 name:value 形态,且没有代码常见标记。
        bool declarative = segments.Count >= 2
            && segments.Take(segments.Count - 1).All(s => s.Contains(':') && !s.Contains('{') && !s.Contains('}') && !s.Contains('=') && !s.Contains("::"));
        return declarative ? segments.Select(s => indent + s).ToList() : [line];
    }
}
