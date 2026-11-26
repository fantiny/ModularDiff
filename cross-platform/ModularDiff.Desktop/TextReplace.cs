using System.Text;
using ModularDiff.Contracts;

namespace ModularDiff.Desktop;

/// <summary>查找替换的文本算术:不碰窗口,也不碰缓冲区。窗口问它"屏幕上这一行在文件里的哪里""全部替换之后文件是什么样",
/// 再像其它修改一样经 EditSide 写进去 —— 撤销、脏标记、编辑页同步、行跟着文字走,都是现成的那一套。</summary>
internal static class TextReplace
{
    /// <summary><paramref name="query"/> 在 <paramref name="text"/> 里的每一处:互不重叠,每次从上一处之后找起 —— 全部替换就是这么走的。</summary>
    internal static List<int> Occurrences(string text, string query, StringComparison comparison)
    {
        var found = new List<int>();
        if (query.Length == 0) return found;
        for (int at = text.IndexOf(query, comparison); at >= 0; at = text.IndexOf(query, at + query.Length, comparison)) found.Add(at);
        return found;
    }

    /// <summary>把每一处都换成 <paramref name="replacement"/>;一处都没有时原样返回,<paramref name="count"/> 为 0。</summary>
    internal static string ReplaceAll(string text, string query, string replacement, StringComparison comparison, out int count)
    {
        var found = Occurrences(text, query, comparison);
        count = found.Count;
        if (count == 0) return text;
        var builder = new StringBuilder(text.Length + (replacement.Length - query.Length) * count);
        int at = 0;
        foreach (int start in found) { builder.Append(text, at, start - at).Append(replacement); at = start + query.Length; }
        return builder.Append(text, at, text.Length - at).ToString();
    }

    /// <summary>第 <paramref name="lineNumber"/> 行(从 1 数)在 <paramref name="text"/> 里的起点和它不含行尾的文字;没有这一行为 null。</summary>
    internal static (int Offset, string Text)? Line(string text, int lineNumber)
    {
        var lines = DocumentLines.Parse(text);
        if (lineNumber < 1 || lineNumber > lines.Count) return null;
        return (lines[lineNumber - 1].Offset, lines[lineNumber - 1].Text);
    }

    /// <summary>把屏幕上的一处匹配换掉之后整个文件的内容。<paramref name="cell"/> 是这一行在屏幕上的样子:它必须和文件里的这一行一字不差,
    /// 屏幕上叫匹配的那几个字也必须真的在那里。对不上返回 null —— 一个字都不写。</summary>
    internal static string? ReplaceOne(string text, TextCell cell, int start, int length, string query, string replacement, StringComparison comparison)
    {
        if (Line(text, cell.LineNumber) is not { } line || line.Text != cell.Text) return null;
        if (start < 0 || length != query.Length || start + length > cell.Text.Length) return null;
        if (!cell.Text.AsSpan(start, length).Equals(query, comparison)) return null;
        int offset = line.Offset + start;
        return string.Concat(text.AsSpan(0, offset), replacement, text.AsSpan(offset + length));
    }

    /// <summary>匹配按行、左先于右、起点的顺序排着:<paramref name="row"/> 这一行 <paramref name="left"/> 这一侧从 <paramref name="start"/> 起,
    /// 第一处不早于它的匹配是第几个;后面没有了就回到第一处。一个匹配都没有为 -1。</summary>
    internal static int AtOrAfter(IReadOnlyList<(int Row, bool Left, int Start, int Length)> matches, int row, bool left, int start)
    {
        for (int i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            // On the same line the left side comes before the right one: only from the left is the right side "after".
            bool after = match.Row > row || (match.Row == row && (match.Left == left ? match.Start >= start : left));
            if (after) return i;
        }
        return matches.Count > 0 ? 0 : -1;
    }
}
