namespace ModularDiff.Core;

/// <summary>行尾感知的文本组装助手 —— 所有宿主重建缓冲区/行文本必须走这里,禁止各自手写 join。
/// 2026-11 全面审查发现 4 处手写 join 把 CRLF 拼成了 LFCR("p1\n\rp2")后设立本类:行尾约定只允许在这一处出现,
/// 单元测试只需覆盖这里,宿主接线测试断言最终字节即可。</summary>
public static class LineText
{
    /// <summary>文本是否使用 CRLF 行尾(检测到任一 "\r\n" 即认定)。</summary>
    public static bool UsesCrlf(string text) => text.Contains("\r\n");

    /// <summary>用裸行(不含行尾符)重建整段文本:沿用原文的 CRLF/LF 风格与"是否以换行结尾"两个属性。
    /// 裸行列表尾部的空串(按 '\n' 拆分产生的尾部空元素)会被规范化掉,结尾统一由原文决定。</summary>
    public static string Rebuild(IReadOnlyList<string> bareLines, string originalText)
    {
        string eol = UsesCrlf(originalText) ? "\r\n" : "\n";
        var lines = new List<string>(bareLines);
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        bool trailing = originalText.EndsWith('\n') || originalText.EndsWith('\r');
        string joined = string.Join(eol, lines);
        return trailing ? joined + eol : joined;
    }

    /// <summary>把一个原始行(可能携带尾部 '\r' —— 宿主按 '\n' 拆分的约定)替换为多个裸行,沿用该行的行尾风格:
    /// CRLF 行 → "p1\r\np2\r",LF 行 → "p1\np2"。</summary>
    public static string ReplaceRawLine(string rawLine, IReadOnlyList<string> parts)
    {
        if (!rawLine.EndsWith('\r')) return string.Join("\n", parts);
        string body = string.Join("\r\n", parts);
        return body + "\r";
    }
}
