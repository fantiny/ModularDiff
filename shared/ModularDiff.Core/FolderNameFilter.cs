using System.Text;
using System.Text.RegularExpressions;

namespace ModularDiff.Core;

/// <summary>VS Code–style path/name filter: substring, glob (* ? **), or .NET regex.</summary>
public sealed class FolderNameFilter
{
    // Takes the relative path already in filter form ('/' separators) and the bare name.
    private readonly Func<string, string, bool> _match;

    private FolderNameFilter(Func<string, string, bool> match, bool isEmpty, string? error)
    {
        _match = match;
        IsEmpty = isEmpty;
        Error = error;
    }

    public bool IsEmpty { get; }
    public string? Error { get; }

    public static FolderNameFilter Create(string query, bool regexMode)
    {
        string trimmed = query.Trim();
        if (trimmed.Length == 0)
            return new((_, _) => true, isEmpty: true, error: null);

        if (regexMode)
        {
            try
            {
                var re = new Regex(trimmed, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
                return new((path, name) => re.IsMatch(path) || re.IsMatch(name), isEmpty: false, error: null);
            }
            catch (ArgumentException ex)
            {
                return new((_, _) => false, isEmpty: false, error: "无效正则：" + ex.Message);
            }
        }

        if (trimmed.IndexOfAny(['*', '?']) < 0)
        {
            string needle = Normalize(trimmed);
            return new((path, name) =>
                path.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || name.Contains(trimmed, StringComparison.OrdinalIgnoreCase), isEmpty: false, error: null);
        }

        var glob = GlobToRegex(Normalize(trimmed));
        return new((path, name) => glob.IsMatch(path) || glob.IsMatch(name), isEmpty: false, error: null);
    }

    public bool Matches(string relativePath, string name) => _match(Normalize(relativePath), name);

    /// <summary><see cref="Matches"/> for a path already in filter form ('/' separators). A filter
    /// box re-evaluates every node on each keystroke; callers that cache the form avoid rebuilding
    /// it for each node.</summary>
    public bool MatchesNormalized(string normalizedPath, string name) => _match(normalizedPath, name);

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static Regex GlobToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                // ** optionally consuming a following slash
                i++;
                if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                {
                    i++;
                    sb.Append("(?:.*/)?");
                }
                else
                    sb.Append(".*");
                continue;
            }
            if (c == '*') { sb.Append("[^/]*"); continue; }
            if (c == '?') { sb.Append("[^/]"); continue; }
            sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
