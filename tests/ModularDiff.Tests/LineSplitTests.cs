using ModularDiff.Contracts;
using ModularDiff.Core;

internal static class LineSplitTests
{
    public static async Task Run(PluginCatalog plugins, Func<string, Func<Task>, Task> test) =>
        await test("Line splitter: css declarations split at top-level semicolons", () =>
        {
            var splitter = plugins.GetAll<ILineSplitter>().FirstOrDefault(s => s.Id == "css-split")
                ?? throw new InvalidOperationException("缺少 css-split 插件。");
            var parts = splitter.Split("    --bg:#0b0d10;--panel:#11151a;--panel2:#171c22;--line:#2a313b;");
            Check(parts.Count == 4, $"expected 4 lines, got {parts.Count}");
            Check(parts[0] == "    --bg:#0b0d10;" && parts[3] == "    --line:#2a313b;", "indent or content lost");
            Check(splitter.Split("--code:#0b0b11}").Count == 1, "single declaration must not split");
            var url = splitter.Split("background:url(\"a;b.png\") repeat;--x:1;");
            Check(url.Count == 2 && url[0].Contains("\"a;b.png\""), "quoted semicolon must not split");
            Check(splitter.CanSplit("a.cs", "int a=1;int b=2;") == false, "code with '=' must not split");
            Check(splitter.CanSplit("a.cpp", "std::string s; s = x;") == false, "'::' must not split");
            Check(splitter.Split("plain text").Count == 1, "no declarations, no split");
            return Task.CompletedTask;
        });
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
