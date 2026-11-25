using System.Text;
using System.Text.RegularExpressions;

namespace ModularDiff.Core;

/// <summary>Translates strings that originate in Core or plugins — exception messages, comparison
/// notes, context-menu action labels, display names — into the host's UI language. Core and plugins
/// stay UI-neutral: each emits one stable source string; the translation table lives in the
/// declarative language packs ("messages" section: source → translation) and this class performs
/// the lookup, so a plugin speaks another language without recompilation.
/// <para>A source may contain "{0}", "{1}"… placeholders: each matches a non-greedy run of characters
/// and is re-inserted into the translation at the same index (reordering is allowed). Captured parts
/// are translated recursively, so a composed message such as "left: 不是有效的 xlsx。" translates both
/// layers. Unknown strings are returned unchanged.</para></summary>
public sealed class MessageCatalog
{
    private static readonly Regex Placeholder = new(@"\{(\d+)\}", RegexOptions.CultureInvariant);
    private readonly Dictionary<string, string> _exact = new(StringComparer.Ordinal);
    private readonly (Regex Pattern, string Template)[] _patterns;

    private MessageCatalog(Dictionary<string, string> exact, (Regex Pattern, string Template)[] patterns)
    {
        _exact = exact; _patterns = patterns;
    }

    public static MessageCatalog Empty { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal), []);
    public int Count => _exact.Count + _patterns.Length;

    public static MessageCatalog Create(IEnumerable<KeyValuePair<string, string>> entries)
    {
        var exact = new Dictionary<string, string>(StringComparer.Ordinal);
        var patterns = new List<(Regex Pattern, string Template, int LiteralLength)>();
        foreach (var (source, target) in entries)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target) || source == target) continue;
            var placeholders = Placeholder.Matches(source);
            if (placeholders.Count == 0) { exact[source] = target; continue; }
            var pattern = new StringBuilder("^");
            int at = 0, literalLength = 0;
            foreach (Match placeholder in placeholders)
            {
                string literal = source[at..placeholder.Index];
                pattern.Append(Regex.Escape(literal)).Append("([\\s\\S]*?)");   // 捕获可跨行:异常消息、多行备注并不罕见
                literalLength += literal.Length;
                at = placeholder.Index + placeholder.Length;
            }
            string tail = source[at..];
            pattern.Append(Regex.Escape(tail)).Append("\\z");   // 不用 $:它会在末尾换行之前就收尾,把换行留在匹配之外
            patterns.Add((new Regex(pattern.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)), target, literalLength + tail.Length));
        }
        // Most specific first: the entry with the longest literal text wins when several match.
        return new MessageCatalog(exact, [.. patterns.OrderByDescending(p => p.LiteralLength).Select(p => (p.Pattern, p.Template))]);
    }

    public string Translate(string? source) => Translate(source, 0);

    private string Translate(string? source, int depth)
    {
        if (string.IsNullOrEmpty(source) || depth > 3) return source ?? "";
        if (_exact.TryGetValue(source, out string? exact)) return exact;
        foreach (var (pattern, template) in _patterns)
        {
            Match match;
            try { match = pattern.Match(source); }
            catch (RegexMatchTimeoutException) { continue; }
            if (!match.Success) continue;
            var parts = new string[match.Groups.Count - 1];
            for (int i = 1; i < match.Groups.Count; i++) parts[i - 1] = Translate(match.Groups[i].Value, depth + 1);
            try { return string.Format(template, parts); }
            catch (FormatException) { return source; }
        }
        return source;
    }
}
