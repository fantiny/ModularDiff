using System.Text;
using ModularDiff.Contracts;

namespace ModularDiff.Format.Css;

/// <summary>CSS 规范格式化:选择器与 '{' 之间统一一个空格并独占一行;每条声明独占一行、2 空格 × 花括号深度缩进、
/// 收尾补分号;'}' 回退缩进独占一行;引号与括号内不参与拆分;块注释行原样保留。</summary>
public sealed class CssFormatter : ICodeFormatter
{
    public string Id => "css-format";
    public string DisplayName => "CSS 规范格式化";

    public bool CanFormat(string fileName)
    {
        string name = Path.GetFileName(fileName ?? "");
        return name.EndsWith(".css", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".scss", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> Format(IReadOnlyList<string> lines)
    {
        var output = new List<string>();
        int depth = 0;
        static string Indent(int level) => new(' ', Math.Max(0, level) * 2);
        foreach (var raw in lines)
        {
            string text = (raw ?? "").Trim();
            if (text.Length == 0) { if (output.Count > 0 && output[^1].Length > 0) output.Add(""); continue; }
            while (text.Length > 0)
            {
                if (text.StartsWith("/*")) { output.Add(Indent(depth) + text); break; }
                if (text[0] == '}')
                {
                    depth = Math.Max(0, depth - 1);
                    output.Add(Indent(depth) + "}");
                    text = text[1..].TrimStart();
                    continue;
                }
                int open = IndexOutside(text, '{');
                string head = open >= 0 ? text[..open].TrimEnd() : "";
                if (head.Length > 0 && !head.Contains(';') && !head.Contains('}'))
                {
                    output.Add(Indent(depth) + head + " {");
                    depth++;
                    text = text[(open + 1)..].TrimStart();
                    continue;
                }
                int close = IndexOutside(text, '}');
                string chunk = close >= 0 ? text[..close] : text;
                foreach (var declaration in Declarations(chunk))
                    output.Add(Indent(depth) + declaration);
                text = close >= 0 ? text[close..] : "";
            }
        }
        return output;
    }

    private static int IndexOutside(string text, char target)
    {
        int paren = 0; char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c is '(' or '[') paren++;
            else if (c is ')' or ']') paren = Math.Max(0, paren - 1);
            else if (paren == 0 && c == target) return i;
        }
        return -1;
    }
    private static IEnumerable<string> Declarations(string chunk)
    {
        var current = new StringBuilder();
        int paren = 0; char quote = '\0';
        foreach (char c in chunk)
        {
            if (quote != '\0') { current.Append(c); if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') { quote = c; current.Append(c); continue; }
            if (c is '(' or '[') paren++;
            else if (c is ')' or ']') paren = Math.Max(0, paren - 1);
            if (c == ';' && paren == 0)
            {
                var part = current.ToString().Trim();
                if (part.Length > 0) yield return part + ";";
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        var tail = current.ToString().Trim();
        if (tail.Length > 0) yield return tail.EndsWith(';') ? tail : tail + ";";
    }
}
