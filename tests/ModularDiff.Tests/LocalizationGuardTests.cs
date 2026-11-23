using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModularDiff.Core;

/// <summary>Guards for the localization architecture, so the class of defect "the UI keeps
/// showing the previous language in some places" cannot come back unnoticed:
/// <list type="bullet">
/// <item>XAML carries no human-language text — every caption is a Loc key (a hand-kept list of
/// assignments is what left labels Chinese in English mode);</item>
/// <item>every key referenced from XAML or code exists in the packs (a typo would show as raw text);</item>
/// <item>WPF code-behind has no hard-coded Chinese string literals (smoke tests excepted);</item>
/// <item>every Chinese string emitted by Core/plugins has an English entry in the message table
/// (plugin strings are translated at the host boundary, Core stays UI-neutral).</item>
/// </list></summary>
internal static class LocalizationGuardTests
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase) { "obj", "bin" };

    /// <summary>Chinese sources that are legitimately not translated: console help and document
    /// output are not UI text.</summary>
    private static readonly HashSet<string> SharedAllowList = new(StringComparer.Ordinal)
    {
        "shared/ModularDiff.Cli/Program.cs",
        "shared/plugins/ModularDiff.Format.Html/HtmlFormatter.cs",
    };

    /// <summary>The only Chinese literal allowed in WPF code: the default pack's display name.</summary>
    private static readonly HashSet<(string File, string Text)> GuiAllowList = new()
    {
        ("Controls/Lang.cs", "中文"),
    };

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("MessageCatalog: exact, templated and composed messages; unknown text passes through", () =>
        {
            var catalog = MessageCatalog.Create(new Dictionary<string, string>
            {
                ["修剪行尾空白"] = "Trim trailing whitespace",
                ["左侧路径不存在：{0}"] = "The left path does not exist: {0}",
                ["外层：{0}；结束"] = "Outer: {0}. End",
                ["内层消息"] = "inner message",
                ["{0} / {1}"] = "{1} | {0}",
            });
            Check(catalog.Translate("修剪行尾空白") == "Trim trailing whitespace", "exact match");
            Check(catalog.Translate("左侧路径不存在：C:\\nope") == "The left path does not exist: C:\\nope", "templated message");
            Check(catalog.Translate("外层：内层消息；结束") == "Outer: inner message. End", "composed message: the nested message is translated too");
            Check(catalog.Translate("untouched 文本") == "untouched 文本", "unknown text passes through unchanged");
            Check(catalog.Translate("a / b") == "b | a", "placeholders re-index into the translation");
            Check(catalog.Translate(null) == "" && MessageCatalog.Empty.Translate("x") == "x", "null input and empty catalog");
            Check(catalog.Count == 5, "entry count");
            return Task.CompletedTask;
        });

        await test("Localization guards: XAML declares keys only, keys exist, no hard-coded Chinese, plugin messages translated", () =>
        {
            string repo = RepoRoot();
            string gui = Path.Combine(repo, "windows", "ModularDiff.Gui");
            var issues = new List<string>();
            using var chinese = Pack(repo, "zh-CN");
            using var english = Pack(repo, "en-US");
            var keys = Strings(chinese);
            var englishMessages = Messages(english);

            // ── 1. XAML carries no human-language text; every declared Loc key exists ──
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (name, text) in Sources(gui, "*.xaml"))
            {
                var withoutComments = Regex.Replace(text, @"<!--[\s\S]*?-->", "");
                int at = IndexOfChinese(withoutComments);
                if (at >= 0) issues.Add($"{name}:{LineOf(withoutComments, at)} — XAML must declare a Loc key (ui:Loc.Key / ui:Loc.TipKey), not literal text");
                foreach (Match key in Regex.Matches(text, @"ui:Loc\.(?:Key|TipKey)=""([A-Za-z0-9_]+)"""))
                    referenced.Add(key.Groups[1].Value);
            }

            // ── 2. WPF code-behind: no hard-coded Chinese; keys referenced from code exist ──
            foreach (var (name, text) in Sources(gui, "*.cs"))
            {
                bool smoke = name.Contains("Smoke", StringComparison.Ordinal);
                foreach (var (line, literal) in StringLiterals(text))
                {
                    if (!smoke && HasChinese(literal) && !GuiAllowList.Contains((name, literal)))
                        issues.Add($"{name}:{line} — hard-coded Chinese in UI code: \"{Truncate(literal)}\" (use Lang.Get / Loc keys / Lang.Text)");
                    // Identifier-like literals with underscores are pack keys by convention (a typo would show
                    // up as raw key text in the UI); a trailing underscore marks a key prefix used with StartsWith.
                    foreach (Match key in Regex.Matches(literal, @"^(?:[a-z]+_)+[a-z0-9_]+$"))
                        if (!key.Value.EndsWith('_') && !keys.ContainsKey(key.Value) && !smoke)
                            issues.Add($"{name}:{line} — key-like literal '{key.Value}' is not a language-pack key (typo?)");
                }
                foreach (Match key in Regex.Matches(text, @"Lang\.Get\(\s*(?:\w+\s*\?\s*)?""([A-Za-z0-9_]+)"""))
                    referenced.Add(key.Groups[1].Value);
                foreach (Match key in Regex.Matches(text, @"Loc\.Set(?:Key|TipKey)\(\s*[^,]+,\s*""([A-Za-z0-9_]+)"""))
                    referenced.Add(key.Groups[1].Value);
            }
            foreach (string key in referenced)
                if (!keys.ContainsKey(key)) issues.Add($"Loc key '{key}' is missing from the zh-CN pack");

            // ── 3. English text must be Chinese-free ──
            foreach (var (key, value) in Strings(english))
                if (HasChinese(value)) issues.Add($"lang-en-US.json: '{key}' still contains Chinese");

            // ── 4. Every Chinese string emitted by Core/plugins has an English entry ──
            var englishSources = englishMessages.Keys.ToHashSet(StringComparer.Ordinal);
            foreach (var (name, text) in Sources(Path.Combine(repo, "shared"), "*.cs", "shared"))
            {
                if (SharedAllowList.Contains(name)) continue;
                foreach (var (line, literal) in StringLiterals(text))
                {
                    if (!literal.Any(c => c is >= '\u4e00' and <= '\u9fff')) continue;
                    // Exact, or a fragment of a composed template (prefix/suffix concatenation in the plugin).
                    if (!englishSources.Any(source => source == literal || source.Contains(literal)))
                        issues.Add($"{name}:{line} — Chinese text from Core/plugins without an English entry in lang-en-US.json \"messages\": \"{Truncate(literal)}\"");
                }
            }
            foreach (string code in new[] { "ja-JP", "ko-KR" })
            {
                using var pack = Pack(repo, code);
                var missing = englishSources.Except(Messages(pack).Keys);
                if (missing.Any()) issues.Add($"lang-{code}.json is missing message entries: {string.Join(", ", missing.Take(5))}");
            }
            using var traditional = Pack(repo, "zh-TW");
            var traditionalMissing = englishSources.Except(Messages(traditional).Keys);
            if (traditionalMissing.Any()) issues.Add($"lang-zh-TW.json is missing message entries: {string.Join(", ", traditionalMissing.Take(5))}");

            Check(issues.Count == 0, string.Join("\n  ", issues));
            return Task.CompletedTask;
        });
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static string RepoRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static string Truncate(string text) => text.Length <= 60 ? text : text[..60] + "…";
    private static bool HasChinese(string text) => IndexOfChinese(text) >= 0;
    private static int IndexOfChinese(string text)
    {
        for (int i = 0; i < text.Length; i++)
            if (text[i] is >= '\u4e00' and <= '\u9fff' or >= '\u3000' and <= '\u303f' or >= '\uff00' and <= '\uffef') return i;
        return -1;
    }
    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private static IEnumerable<(string Name, string Text)> Sources(string root, string pattern, string? prefix = null)
    {
        foreach (string file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string name = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (name.Split('/').Any(part => SkippedDirectories.Contains(part))) continue;
            yield return (prefix is null ? name : $"{prefix}/{name}", File.ReadAllText(file));
        }
    }

    private static JsonDocument Pack(string repo, string code) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "windows", "ModularDiff.Gui", "languages", $"lang-{code}.json")));

    private static Dictionary<string, string> Strings(JsonDocument pack) =>
        pack.RootElement.GetProperty("strings").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);

    private static Dictionary<string, string> Messages(JsonDocument pack) =>
        pack.RootElement.GetProperty("messages").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);

    /// <summary>String literals of a C# source outside comments: (line, text). Regular, verbatim,
    /// interpolated and raw literals are recognized; interpolation holes become {0}, {1}… so that a
    /// literal compares with the templates of the message table.</summary>
    private static List<(int Line, string Text)> StringLiterals(string code)
    {
        var result = new List<(int, string)>();
        int index = 0;
        ScanCode(code, ref index, null);
        return result;

        void ScanCode(string s, ref int i, char? stop)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (stop is char end && c == end) return;
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; }
                else if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                { int close = s.IndexOf("*/", i + 2, StringComparison.Ordinal); i = close < 0 ? s.Length : close + 2; }
                else if (c == '"') ScanString(s, ref i);
                else if (c == '\'')
                { i++; while (i < s.Length && s[i] != '\'' && s[i] != '\n') i += s[i] == '\\' && i + 1 < s.Length ? 2 : 1; i++; }
                else if (c == '{') { i++; ScanCode(s, ref i, '}'); if (i < s.Length && s[i] == '}') i++; }
                else i++;
            }
        }

        void ScanString(string s, ref int i)
        {
            int start = i;
            bool verbatim = false, interpolated = false;
            for (int k = start - 1; k >= 0 && s[k] is '$' or '@'; k--)
            { verbatim |= s[k] == '@'; interpolated |= s[k] == '$'; }
            int quotes = 0;
            while (start + quotes < s.Length && s[start + quotes] == '"') quotes++;
            int line = LineOf(s, start);
            if (quotes >= 3)   // raw string literal
            {
                string opener = new('"', quotes);
                int close = s.IndexOf(opener, start + quotes, StringComparison.Ordinal);
                int end = close < 0 ? s.Length : close + quotes;
                while (end < s.Length && s[end] == '"') end++;
                result.Add((line, s[(start + quotes)..(close < 0 ? s.Length : close)]));
                i = end;
                return;
            }
            var text = new StringBuilder();
            i++;
            int hole = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (!verbatim && c == '\\') { text.Append(s, i, Math.Min(2, s.Length - i)); i += 2; continue; }
                if (c == '"')
                {
                    if (verbatim && i + 1 < s.Length && s[i + 1] == '"') { text.Append("\"\""); i += 2; continue; }
                    i++;
                    break;
                }
                if (interpolated && c == '{')
                {
                    if (i + 1 < s.Length && s[i + 1] == '{') { text.Append("{{"); i += 2; continue; }
                    text.Append('{').Append(hole++).Append('}');
                    i++;
                    ScanCode(s, ref i, '}');
                    if (i < s.Length && s[i] == '}') i++;
                    continue;
                }
                text.Append(c); i++;
            }
            string literal = text.ToString();
            if (verbatim) literal = literal.Replace("\"\"", "\"");
            else literal = literal.Replace("\\\"", "\"").Replace("\\\\", "\\");
            result.Add((line, literal));
        }
    }
}
