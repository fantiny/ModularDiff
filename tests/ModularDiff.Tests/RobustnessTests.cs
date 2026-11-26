using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModularDiff.Core;

/// <summary>共享层小工具的健壮性:它们都跑在界面线程上、对用户输入逐键执行,
/// 一旦阻塞或抛异常就是整窗卡死或崩溃 —— 之前都没有测试覆盖。</summary>
internal static class RobustnessTests
{
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("FolderNameFilter: a pathological pattern times out instead of freezing the UI", () =>
        {
            string name = new string('a', 60) + "b";   // 让 (a+)+$ 指数级回溯的经典输入
            var evil = FolderNameFilter.Create("(a+)+$", regexMode: true);
            Check(evil.Error is null, "the pattern itself is valid");
            var watch = Stopwatch.StartNew();
            bool matched = evil.Matches("src/x.txt", name);
            watch.Stop();
            Check(!matched && watch.Elapsed < TimeSpan.FromSeconds(3), $"a catastrophic-backtracking regex must give up quickly (took {watch.Elapsed.TotalSeconds:0.00}s, matched={matched})");
            // 通配符 *a 重复十次再加一个不存在的 b:多项式回溯,没有预算时同样会把界面卡死。
            string noB = new string('a', 60);
            var glob = FolderNameFilter.Create("*a*a*a*a*a*a*a*a*a*a*b", regexMode: false);
            watch.Restart();
            matched = glob.Matches("src/x.txt", noB);
            watch.Stop();
            Check(!matched && watch.Elapsed < TimeSpan.FromSeconds(3), $"a pathological glob must give up quickly (took {watch.Elapsed.TotalSeconds:0.00}s, matched={matched})");
            Check(FolderNameFilter.Create(name, regexMode: false).Matches("src/x.txt", name) && FolderNameFilter.Create("*b", regexMode: false).Matches("src/x.txt", name),
                "ordinary filters keep working after a pathological one timed out");
            return Task.CompletedTask;
        });

        await test("FolderNameFilter: edge cases of the glob translation", () =>
        {
            var anyCs = FolderNameFilter.Create("*.cs", regexMode: false);
            Check(!anyCs.Matches("src/a.csproj", "a.csproj"), "*.cs must not match .csproj");
            Check(anyCs.Matches("A/B/C.CS", "C.CS"), "*.cs matches at any depth, case-insensitively");
            var one = FolderNameFilter.Create("a?c.txt", regexMode: false);
            Check(one.Matches("abc.txt", "abc.txt") && !one.Matches("a/c.txt", "c.txt"), "? matches one character but not a separator");
            var deep = FolderNameFilter.Create("src/**/*.cs", regexMode: false);
            Check(deep.Matches("src/a.cs", "a.cs") && deep.Matches("src/x/y/z.cs", "z.cs"), "'**/' may match zero folders");
            Check(!deep.Matches("other/a.cs", "a.cs"), "anchored at the start");
            var meta = FolderNameFilter.Create("a+b(1).txt", regexMode: false);
            Check(meta.Matches("dir/a+b(1).txt", "a+b(1).txt") && !meta.Matches("dir/aab1.txt", "aab1.txt"), "regex metacharacters are literal in glob mode");
            return Task.CompletedTask;
        });

        await test("MessageCatalog: captured values may span lines; a trailing newline is kept", () =>
        {
            var catalog = MessageCatalog.Create(new Dictionary<string, string>
            {
                ["前缀：{0}"] = "Prefix: {0}",
                ["结尾 {0}"] = "Tail {0}",
            });
            Check(catalog.Translate("前缀：第一行\n第二行") == "Prefix: 第一行\n第二行", $"a value with a newline must still be captured: {catalog.Translate("前缀：第一行\n第二行")}");
            Check(catalog.Translate("结尾 abc\n") == "Tail abc\n", "a trailing newline in the source must survive the translation");
            Check(catalog.Translate("前缀：") == "Prefix: ", "an empty capture is allowed (unchanged from the original behaviour)");
            return Task.CompletedTask;
        });

        await test("Language packs: every text takes the same arguments in every language", () =>
        {
            // A translation that drops a {0} or renumbers it shows the wrong value, or throws when formatted — in that language only.
            string languages = Path.Combine(DesktopGuardTests.RepoRoot(), "windows", "ModularDiff.Gui", "languages");
            var placeholder = new Regex(@"\{(\d+)(?:[^}]*)?\}", RegexOptions.CultureInvariant);
            string[] Arguments(string? text) => [.. placeholder.Matches(text ?? "").Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal)];
            // The guard must be able to fail: it sees repeated, formatted and absent arguments.
            Check(Arguments("Line {1} is not on the {0} ({0:N0})").SequenceEqual(["0", "0", "1"]) && Arguments("no arguments here { }").Length == 0,
                "the scan should list every placeholder of a text, repeated and formatted ones included");

            var packs = Directory.EnumerateFiles(languages, "lang-*.json").Order(StringComparer.Ordinal)
                .Select(file => (File: Path.GetFileName(file), Pack: JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(file))!)).ToArray();
            Check(packs.Length == 5, $"five packs expected under {languages}, found {packs.Length}");
            var problems = new List<string>();
            foreach (string table in new[] { "strings", "messages" })
            {
                // The message tables translate different sources per language, so a key may be missing from some of them;
                // where a key is, its text must take the arguments the others take.
                var keys = packs.SelectMany(pack => pack.Pack[table].EnumerateObject().Select(property => property.Name)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
                foreach (string key in keys)
                {
                    var texts = packs.Select(pack => (pack.File, Text: pack.Pack[table].TryGetProperty(key, out var value) ? value.GetString() : null)).Where(entry => entry.Text is not null).ToArray();
                    string[] reference = Arguments(texts[0].Text);
                    foreach (var (file, text) in texts.Skip(1))
                        if (!Arguments(text).SequenceEqual(reference))
                            problems.Add($"{table} '{key}': {texts[0].File} takes {{{string.Join(", ", reference)}}}, {file} takes {{{string.Join(", ", Arguments(text))}}}");
                }
            }
            Check(problems.Count == 0, "texts that format different arguments in different languages:\n  " + string.Join("\n  ", problems.Take(12)));
            return Task.CompletedTask;
        });
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
