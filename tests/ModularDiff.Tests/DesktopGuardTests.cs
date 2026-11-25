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
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = StripComment(lines[i]);
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
    }

    private static bool InBinOrObj(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj");

    private static string StripComment(string line)
    {
        int index = line.IndexOf("//", StringComparison.Ordinal);
        return index < 0 ? line : line[..index];
    }

    internal static string RepoRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
