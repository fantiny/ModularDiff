using ModularDiff.Contracts;
using ModularDiff.Core;

internal static class LineSplitTests
{
    public static async Task Run(PluginCatalog plugins, Func<string, Func<Task>, Task> test)
    {
        await test("Line splitter: css declarations split at top-level semicolons", () =>
        {
            var splitter = plugins.GetAll<ILineSplitter>().FirstOrDefault(s => s.Id == "css-split")
                ?? throw new InvalidOperationException("缺少 css-split 插件。");
            var parts = splitter.Split("    --bg:#0b0d10;--panel:#11151a;--panel2:#171c22;--line:#2a313b;");
            Check(parts.Count == 4, $"expected 4 lines, got {parts.Count}");
            Check(parts[0] == "    --bg:#0b0d10;" && parts[3] == "    --line:#2a313b;", "indent or content lost");
            Check(splitter.Split("--code:#0b0b11}").Count == 2, "trailing brace moves to its own line");
            var url = splitter.Split("background:url(\"a;b.png\") repeat;--x:1;");
            Check(url.Count == 2 && url[0].Contains("\"a;b.png\""), "quoted semicolon must not split");
            Check(splitter.CanSplit("a.cs", "int a=1;int b=2;") == false, "code with '=' must not split");
            Check(splitter.CanSplit("a.cpp", "std::string s; s = x;") == false, "'::' must not split");
            Check(splitter.Split("plain text").Count == 1, "no declarations, no split");
            Check(splitter.Split("--a:1;").Count == 1, "single declaration stays on one line");
            return Task.CompletedTask;
        });
        await test("Line splitter: selector opener goes on its own line", () =>
        {
            var splitter = plugins.Get<ILineSplitter>("css-split");
            var opened = splitter.Split("body{margin:0;background:var(--bg);color:var(--text);");
            Check(opened.Count == 4 && opened[0] == "body{" && opened[1] == "margin:0;" && opened[2] == "background:var(--bg);" && opened[3] == "color:var(--text);",
                $"unexpected split: {string.Join(" | ", opened)}");
            var spaced = splitter.Split("body {margin:0;}");
            Check(spaced.Count == 3 && spaced[0] == "body {" && spaced[1] == "margin:0;" && spaced[2] == "}", "opener spacing preserved, closing brace on its own line");
            Check(splitter.CanSplit("a.html", "console.log(a);console.log(b);") == false, "js calls without ':' must not split");
            return Task.CompletedTask;
        });
        await test("Code formatter: css rules normalized to standard shape", () =>
        {
            var formatter = plugins.GetAll<ICodeFormatter>().FirstOrDefault(f => f.Id == "css-format")
                ?? throw new InvalidOperationException("缺少 css-format 插件。");
            Check(formatter.CanFormat("a.css") && formatter.CanFormat("b.SCSS") && !formatter.CanFormat("a.html"), "formatter scope is css/scss");
            var formatted = formatter.Format(new[] { "body{margin:0;color:var(--t)}", "/* keep me */", ".card{padding:2px;}" });
            Check(formatted.SequenceEqual(new[] { "body {", "  margin:0;", "  color:var(--t);", "}", "/* keep me */", ".card {", "  padding:2px;", "}" }),
                $"unexpected format: {string.Join(" | ", formatted)}");
            var nested = formatter.Format(new[] { "@media print{.a{color:red}}" });
            Check(nested.SequenceEqual(new[] { "@media print {", "  .a {", "    color:red;", "  }", "}" }), "nested blocks indent by depth");
            return Task.CompletedTask;
        });
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
