using System.Diagnostics;
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
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
