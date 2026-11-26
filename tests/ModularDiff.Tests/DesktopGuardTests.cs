using System.Text;
using System.Text.RegularExpressions;

/// <summary>macOS(Desktop)界面的守卫测试。上游的本地化守卫只扫 WPF 工程,
/// Desktop 里打错一个语言键不会有任何测试失败 —— 只会在界面上把键名原样显示给用户。</summary>
internal static class DesktopGuardTests
{
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Desktop localization guard: every pack key used by the Desktop GUI exists, in every language", () =>
        {
            string repo = RepoRoot();
            string desktop = Path.Combine(repo, "cross-platform", "ModularDiff.Desktop");
            string languages = Path.Combine(repo, "windows", "ModularDiff.Gui", "languages");

            // 键的字面形式:小写单词用下划线连接。以额外下划线结尾的是"前缀"(运行时拼接),不算完整的键。
            var keyLiteral = new Regex("^(?:[a-z]+_)+[a-z0-9_]+$", RegexOptions.CultureInvariant);
            var sources = Directory.EnumerateFiles(desktop, "*.cs", SearchOption.AllDirectories).Where(f => !InBinOrObj(f)).ToArray();
            Check(sources.Length >= 5, $"expected the Desktop sources under {desktop}");

            var used = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string file in sources)
            {
                string[] lines = CodeOnly(File.ReadAllText(file)).Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    foreach (System.Text.RegularExpressions.Match literal in Regex.Matches(line, "\"([A-Za-z0-9_]+)\""))
                    {
                        string value = literal.Groups[1].Value;
                        if (!keyLiteral.IsMatch(value) || value.EndsWith('_')) continue;
                        if (!used.TryGetValue(value, out var where)) used[value] = where = [];
                        where.Add($"{Path.GetFileName(file)}:{i + 1}");
                    }
                }
            }
            // The keys the sources spell out must be found; a literal ending in '_' is only the front of a key joined at
            // run time (Lang.PluginText tests for "sync_" and "file_status_"), and must not be reported as missing.
            Check(used.ContainsKey("md_title") && used.ContainsKey("md_tip_realign") && used.ContainsKey("file_status_modified"),
                "the scan should find the pack keys the Desktop sources spell out");
            Check(!used.ContainsKey("file_status_") && !used.ContainsKey("sync_"), "the scan should skip prefixes joined at run time");

            var problems = new List<string>();
            foreach (string packFile in Directory.EnumerateFiles(languages, "lang-*.json").Order(StringComparer.Ordinal))
            {
                using var pack = System.Text.Json.JsonDocument.Parse(File.ReadAllText(packFile));
                var keys = pack.RootElement.GetProperty("strings").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var (key, where) in used)
                    if (!keys.Contains(key)) problems.Add($"{Path.GetFileName(packFile)} has no '{key}' (used at {where[0]})");
            }
            Check(problems.Count == 0, "Desktop uses pack keys that a language pack is missing (the raw key would be shown):\n  " + string.Join("\n  ", problems.Take(12)));
            return Task.CompletedTask;
        });

        await test("Desktop localization guard: the Desktop GUI has no text of its own — what it shows comes from the language packs", () =>
        {
            // A caption or message written into a source file is Chinese in every UI language; the packs are the only
            // place for it. Comments are free: what is checked is the code, with its comments taken out.
            string desktop = Path.Combine(RepoRoot(), "cross-platform", "ModularDiff.Desktop");
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "中文" };   // the name of the default pack, in Lang.cs
            var userLanguage = new Regex("[\\u3040-\\u30ff\\u3400-\\u9fff\\uac00-\\ud7af]", RegexOptions.CultureInvariant);

            // The guard must be able to fail: on these it does, and a '//' inside a string is not taken for a comment.
            Check(!userLanguage.IsMatch(CodeOnly("int done = 1; // 已完成")), "a line comment is not code");
            Check(!userLanguage.IsMatch(CodeOnly("/* 已实时提交 */ int a = 1;")) && !userLanguage.IsMatch(CodeOnly("/* 多行\n * 注释 */ int a = 1;")),
                "a block comment is not code, not even across lines");
            Check(userLanguage.IsMatch(CodeOnly("_status.Text = \"请先打开文本比较。\";")) && userLanguage.IsMatch(CodeOnly("var url = \"http://x/y\"; var tip = \"提示\";")),
                "text in a string literal is found, also after a string that contains '//'");
            Check(!userLanguage.IsMatch(CodeOnly("if (c == '\"') return; // 引号")), "a quote character literal does not open a string");
            Check(userLanguage.IsMatch(CodeOnly("int n = 1;\n_status.Text = \"提示\"; // 说明\nint m = 2;").Split('\n')[1]) && !userLanguage.IsMatch(CodeOnly("int n = 1;\n// 提示\nint m = 2;").Split('\n')[1]),
                "line comments end with their line");

            var found = new List<string>();
            foreach (string file in Directory.EnumerateFiles(desktop, "*.cs", SearchOption.AllDirectories).Where(f => !InBinOrObj(f)).Order(StringComparer.Ordinal))
            {
                string[] source = File.ReadAllLines(file), lines = CodeOnly(File.ReadAllText(file)).Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string code = lines[i];
                    foreach (string text in allowed) code = code.Replace($"\"{text}\"", "");
                    if (userLanguage.IsMatch(code))
                    {
                        string shown = source[i].Trim();
                        found.Add($"{Path.GetFileName(file)}:{i + 1}  {shown[..Math.Min(shown.Length, 90)]}");
                    }
                }
            }
            Check(found.Count == 0, $"text of its own in the Desktop GUI (it would stay Chinese in every language):\n  " + string.Join("\n  ", found.Take(60)));
            return Task.CompletedTask;
        });
    }

    private static bool InBinOrObj(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj");

    /// <summary>The source with its comments blanked out, newlines kept, so lines can still be told apart.
    /// A "//" inside a string literal is not a comment; a quote written as a character literal does not open a string.</summary>
    private static string CodeOnly(string source)
    {
        var text = new StringBuilder(source.Length);
        bool quoted = false, block = false, line = false;
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (line || block)
            {
                if (line && c == '\n') line = false;
                else if (block && c == '*' && i + 1 < source.Length && source[i + 1] == '/') { block = false; i++; text.Append("  "); continue; }
                text.Append(c == '\n' ? '\n' : ' ');
                continue;
            }
            if (c == '\\' && quoted && i + 1 < source.Length) { text.Append(c).Append(source[i + 1]); i++; continue; }   // an escaped character inside a literal
            if (c == '\'' && i + 2 < source.Length && source[i + 1] == '"' && source[i + 2] == '\'') { text.Append("'\"'"); i += 2; continue; }
            if (c == '"') quoted = !quoted;
            else if (!quoted && c == '/' && i + 1 < source.Length && source[i + 1] == '/') { line = true; text.Append("  "); i++; continue; }
            else if (!quoted && c == '/' && i + 1 < source.Length && source[i + 1] == '*') { block = true; text.Append("  "); i++; continue; }
            text.Append(c);
        }
        return text.ToString();
    }

    internal static string RepoRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
