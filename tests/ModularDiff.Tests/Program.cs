using System.Text;
using System.Text.Json;
using ModularDiff.Contracts;
using ModularDiff.Core;

if (args.Length != 1) { Console.Error.WriteLine("Usage: ModularDiff.Tests PLUGIN_ROOT"); return 2; }
int passed = 0, failed = 0;
using var catalog = PluginCatalog.Load(Path.GetFullPath(args[0]));
var engine = new ComparisonEngine(catalog);
string temp = Path.Combine(Path.GetTempPath(), "ModularDiff.Tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    await Test("Runtime discovery and shared contract identity", () =>
    {
        Check(catalog.Diagnostics.Count == 0, string.Join("; ", catalog.Diagnostics));
        Check(catalog.GetAll<IDiffAlgorithm>().Count >= 2, "Expected the built-in algorithms.");
        Check(catalog.GetAll<IComparisonModule>().Count >= 2, "Expected the built-in modules.");
        Check(catalog.GetAll<IResultFormatter>().Count >= 2, "Expected the built-in formatters.");
        Check(catalog.Get<IDiffAlgorithm>("MYERS").Id == "myers", "Case-insensitive lookup failed.");
        return Task.CompletedTask;
    });

    await EditingTests.Run(catalog, temp, Test);
    await SyncTests.Run(catalog, temp, Test);
    await SafetyTests.Run(catalog, temp, Test);
    await MenuPlanTests.Run(Test);
    await SessionStoreTests.Run(Test, temp);
    await Test("myers: budget fallback anchors unique common lines", () =>
    {
        var algorithm = catalog.Get<IDiffAlgorithm>("myers");
        var random = new Random(9);
        var left = new List<string>(); var right = new List<string>();
        for (int block = 0; block < 60; block++)
        {
            for (int i = 0; i < 140; i++) left.Add($"left-{block}-{i}-{random.Next():x8}");
            for (int i = 0; i < 140; i++) right.Add($"right-{block}-{i}-{random.Next():x8}");
            left.Add($"COMMON-ANCHOR-{block:000}"); right.Add($"COMMON-ANCHOR-{block:000}");
        }
        var result = algorithm.Compare(left, right, catalog);
        Check(result.IsApproximate, "expected the trace budget to be exceeded");
        var anchors = result.Edits.Where(e => e.Kind == EditKind.Equal && left[e.LeftIndex!.Value].StartsWith("COMMON-ANCHOR")).Count();
        Check(anchors == 60, $"expected 60 anchored equals, got {anchors}");
        // 编辑脚本必须能完整重建两侧输入(兜底路径也不例外)。
        var rebuiltLeft = new List<string>(); var rebuiltRight = new List<string>();
        int li = 0, ri = 0;
        foreach (var edit in result.Edits)
        {
            if (edit.Kind == EditKind.Equal) { rebuiltLeft.Add(left[li++]); rebuiltRight.Add(right[ri++]); }
            else if (edit.Kind == EditKind.Delete) rebuiltLeft.Add(left[li++]);
            else rebuiltRight.Add(right[ri++]);
        }
        Check(rebuiltLeft.SequenceEqual(left) && rebuiltRight.SequenceEqual(right), "fallback edits must reconstruct both inputs");
        return Task.CompletedTask;
    });

    await LineSplitTests.Run(catalog, Test);

    foreach (var id in new[] { "myers", "patience" })
    {
        await Test($"{id}: 1000 seeded sequences reconstruct both inputs" + (id == "myers" ? " and match independent LCS oracle" : ""), () =>
        {
            var algorithm = catalog.Get<IDiffAlgorithm>(id); var random = new Random(72026);
            for (int trial = 0; trial < 1000; trial++)
            {
                var left = Enumerable.Range(0, random.Next(0, 32)).Select(_ => ((char)('a' + random.Next(7))).ToString()).ToArray();
                var right = Enumerable.Range(0, random.Next(0, 32)).Select(_ => ((char)('a' + random.Next(7))).ToString()).ToArray();
                var result = algorithm.Compare(left, right, catalog);
                Validate(left, right, result);
                Check(!result.IsApproximate, "Small input unexpectedly exceeded budget.");
                if (id == "myers") Check(result.Edits.Count(e => e.Kind != EditKind.Equal) == Distance(left, right), $"Nonminimal Myers result on trial {trial}.");
            }
            return Task.CompletedTask;
        });
        await Test($"{id}: cancellation", () =>
        {
            using var cts = new CancellationTokenSource(); cts.Cancel();
            Throws<OperationCanceledException>(() => catalog.Get<IDiffAlgorithm>(id).Compare(["a"], ["b"], catalog, cts.Token));
            return Task.CompletedTask;
        });
    }
    await Test("Myers resource cap preserves all lines with explicit coarse-result flag", () =>
    {
        string[] left = Enumerable.Range(0, 8000).Select(i => "left" + i).ToArray();
        string[] right = Enumerable.Range(0, 8000).Select(i => "right" + i).ToArray();
        var result = catalog.Get<IDiffAlgorithm>("myers").Compare(left, right, catalog);
        Check(result.IsApproximate && result.Note is not null, "Budget fallback must be disclosed.");
        Validate(left, right, result); return Task.CompletedTask;
    });
    await Test("Same-length content is actually compared", async () =>
    {
        var result = await Text("abc\n", "axc\n"); Check(result.HasDifferences, "Same size must not imply equality.");
        Check(result.Files[0].Lines.Count(l => l.Kind != EditKind.Equal) == 2, "Expected delete + insert.");
    });
    await Test("Empty files and a real blank line are distinct", async () =>
    {
        Check(!(await Text("", "")).HasDifferences, "Empty pair unequal.");
        Check((await Text("", "\n")).HasDifferences, "Blank line lost.");
    });
    await Test("CRLF / LF / CR / missing final newline", async () =>
    {
        Check((await Text("a\r\n", "a\n")).HasDifferences, "CRLF difference lost.");
        Check((await Text("a\n", "a")).HasDifferences, "Final newline difference lost.");
        Check(!(await Text("a\r", "a\n", new(IgnoreLineEndings: true))).HasDifferences, "Ignore EOL failed.");
        Check(!(await Text("a\n", "a", new(IgnoreLineEndings: true))).HasDifferences, "Ignore final EOL failed.");
    });
    await Test("Case/whitespace options preserve original displayed text", async () =>
    {
        var result = await Text(" A B\n", "ab\n", new(IgnoreCase: true, IgnoreWhitespace: true));
        Check(!result.HasDifferences, "Normalization failed.");
        Check(result.Files[0].Lines[0].LeftText == " A B", "Original text was destroyed.");
    });
    await Test("UTF-16 BOM and UTF-8 decode to equivalent Unicode", async () =>
    {
        var a = Path.Combine(temp, "utf16"); var b = Path.Combine(temp, "utf8");
        await File.WriteAllTextAsync(a, "中文🙂\n", Encoding.Unicode);
        await File.WriteAllTextAsync(b, "中文🙂\n", new UTF8Encoding(false));
        var result = await engine.CompareAsync(new(a, b));
        Check(!result.HasDifferences && !result.Files[0].IsBinary, "Unicode detection failed.");
    });
    await Test("Binary / invalid UTF-8 compare exact bytes", async () =>
    {
        var a = Path.Combine(temp, "bin-a"); var b = Path.Combine(temp, "bin-b");
        await File.WriteAllBytesAsync(a, [0, 1, 255]); await File.WriteAllBytesAsync(b, [0, 2, 255]);
        var result = await engine.CompareAsync(new(a, b));
        Check(result.HasDifferences && result.Files[0].IsBinary, "Binary difference missed.");
        await File.WriteAllBytesAsync(b, [0, 1, 255]);
        Check(!(await engine.CompareAsync(new(a, b))).HasDifferences, "Equal binary files differed.");
        await File.WriteAllBytesAsync(a, [255, 128]); await File.WriteAllBytesAsync(b, [255, 129]);
        Check((await engine.CompareAsync(new(a, b))).Files[0].IsBinary, "Invalid UTF8 was silently replaced.");
    });
    await Test("Above text limit uses streaming comparison", async () =>
    {
        var result = await Text("a\nb\nc", "a\nb\nd", new(MaxTextBytes: 2));
        Check(result.HasDifferences && result.Files[0].IsBinary && result.Files[0].Lines.Count == 0, "Text size limit ignored.");
    });
    await Test("Directory plugin composes file plugin, distinguishes one-sided/nested files", async () =>
    {
        string left = Path.Combine(temp, "left"), right = Path.Combine(temp, "right");
        Directory.CreateDirectory(Path.Combine(left, "nested")); Directory.CreateDirectory(Path.Combine(right, "nested"));
        await File.WriteAllTextAsync(Path.Combine(left, "same"), "same"); await File.WriteAllTextAsync(Path.Combine(right, "same"), "same");
        await File.WriteAllTextAsync(Path.Combine(left, "only-left"), "x"); await File.WriteAllTextAsync(Path.Combine(right, "only-right"), "y");
        await File.WriteAllTextAsync(Path.Combine(left, "nested", "code"), "aaa"); await File.WriteAllTextAsync(Path.Combine(right, "nested", "code"), "bbb");
        var result = await engine.CompareAsync(new(left, right, "patience"));
        Check(result.Files.Count == 4 && result.Files.Count(f => f.Status == FileStatus.Modified) == 1, "Recursive file pairing failed.");
        Check(result.Files.Count(f => f.Status == FileStatus.LeftOnly) == 1 && result.Files.Count(f => f.Status == FileStatus.RightOnly) == 1, "One-sided files failed.");
        Check(result.Files.All(f => f.Lines.Count == 0), "Directory summaries retained line payloads.");
        result = await engine.CompareAsync(new(left, right, Options: new(Recursive: false)));
        Check(result.Files.Count == 3, "Non-recursive option ignored.");
    });
    await Test("Invalid paths and unavailable algorithms fail explicitly", async () =>
    {
        await ThrowsAsync<InvalidOperationException>(() => engine.CompareAsync(new("/does-not-exist-left", "/does-not-exist-right")));
        await ThrowsAsync<InvalidOperationException>(() => Text("a", "b", algorithm: "missing"));
    });
    await Test("All formatter plugins serialize the real comparison result", async () =>
    {
        var result = await Text("left\n", "right\n");
        using var json = new StringWriter(); await catalog.Get<IResultFormatter>("json").WriteAsync(result, json);
        using var parsed = JsonDocument.Parse(json.ToString());
        Check(parsed.RootElement.GetProperty("HasDifferences").GetBoolean(), "JSON missing status.");
        Check(parsed.RootElement.GetProperty("Files")[0].GetProperty("Status").GetString() == "Modified", "Enums not readable.");
        using var text = new StringWriter(); await catalog.Get<IResultFormatter>("text").WriteAsync(result, text);
        Check(text.ToString().Contains("left") && text.ToString().Contains("right"), "Text edits missing.");
        using var html = new StringWriter(); await catalog.Get<IResultFormatter>("html").WriteAsync(result, html);
        string page = html.ToString();
        Check(page.StartsWith("<!DOCTYPE html>") && page.Contains("<td class=\"code\">left</td>") && page.Contains("<td class=\"code\">right</td>"), "HTML rows missing.");
        Check(!page.Contains("<script"), "Report must stay self-contained.");
        var tagged = await Text("<script>alert(1)</script>\n", "ok\n");
        using var escaped = new StringWriter(); await catalog.Get<IResultFormatter>("html").WriteAsync(tagged, escaped);
        Check(escaped.ToString().Contains("&lt;script&gt;"), "HTML must escape content.");
    });
    await Test("Malformed manifest isolated, good packages still load", () =>
    {
        string root = Path.Combine(temp, "packages"); Directory.CreateDirectory(root);
        foreach (var folder in Directory.GetDirectories(args[0])) CopyDirectory(folder, Path.Combine(root, Path.GetFileName(folder)));
        Directory.CreateDirectory(Path.Combine(root, "broken")); File.WriteAllText(Path.Combine(root, "broken", "plugin.json"), "not json");
        using var loaded = PluginCatalog.Load(root);
        Check(loaded.Diagnostics.Count == 1 && loaded.GetAll<IPlugin>().Count == catalog.GetAll<IPlugin>().Count, "Bad package poisoned discovery.");
        return Task.CompletedTask;
    });
    await Test("Duplicate IDs rejected and unsupported manifest API rejected", () =>
    {
        string root = Path.Combine(temp, "duplicates"); Directory.CreateDirectory(root);
        string source = Path.Combine(args[0], "ModularDiff.Algorithms.Myers");
        CopyDirectory(source, Path.Combine(root, "one")); CopyDirectory(source, Path.Combine(root, "two"));
        Directory.CreateDirectory(Path.Combine(root, "new-api"));
        File.WriteAllText(Path.Combine(root, "new-api", "plugin.json"), "{\"apiVersion\":999,\"assembly\":\"unknown.dll\"}");
        using var loaded = PluginCatalog.Load(root);
        Check(loaded.Diagnostics.Count == 2 && loaded.GetAll<IDiffAlgorithm>().Count == 1, "Version/duplicate handling failed.");
        return Task.CompletedTask;
    });
    await Test("Missing compositional dependency diagnosed before comparison", () =>
    {
        string root = Path.Combine(temp, "missing-dependency");
        CopyDirectory(Path.Combine(args[0], "ModularDiff.Algorithms.Patience"), Path.Combine(root, "patience"));
        using var loaded = PluginCatalog.Load(root);
        Check(loaded.Diagnostics.Count == 1 && loaded.Diagnostics[0].Contains("myers"), "Missing dependency was not reported.");
        return Task.CompletedTask;
    });
    await Test("Presentation pairs similar replacement lines and preserves ghost rows", async () =>
    {
        var result = await Text("public string Version => \"1\";\n", "public int Multiply(int a, int b) => a * b;\n\npublic string Version => \"2\";\n");
        var view = catalog.Get<ITextPresentation>("side-by-side").Build(result.Files[0].Lines);
        Check(view.Hunks.Count == 1 && view.Rows.Count == 3, "Replacement block has wrong size.");
        Check(view.Rows[0].Left is null && view.Rows[1].Left is null, "Inserted lines should have ghost cells.");
        Check(view.Rows[2].Left?.Text.Contains("Version") == true && view.Rows[2].Right?.Text.Contains("Version") == true, "Similar lines were not aligned.");
        Check(view.Rows[2].Left!.Highlights.Count > 0, "Inline change was not highlighted.");
    });
    await Test("Presentation context folding retains every changed row and stable hunk IDs", async () =>
    {
        string a = string.Join('\n', Enumerable.Range(0, 40).Select(i => "line " + i));
        string b = a.Replace("line 10\n", "modified 10\n").Replace("line 30\n", "modified 30\n");
        var result = await Text(a, b); var plugin = catalog.Get<ITextPresentation>("side-by-side");
        var full = plugin.Build(result.Files[0].Lines); var context = plugin.Build(result.Files[0].Lines, 3);
        Check(full.Hunks.Count == 2 && context.Hunks.SequenceEqual(full.Hunks), "Hunk identity changed during filtering.");
        Check(context.Rows.Count < full.Rows.Count && context.Rows.Any(r => r.HiddenLines > 0), "Equal regions did not fold.");
        Check(context.Rows.Count(r => r.IsDifferent) == full.Rows.Count(r => r.IsDifferent), "Context filter lost changes.");
        var changes = plugin.Build(result.Files[0].Lines, 0);
        Check(changes.Rows.All(r => r.IsDifferent || r.HiddenLines > 0), "Changes-only view contains uncollapsed equal rows.");
    });
    await Test("Presentation preserves both sequences across 200 random edit scripts", () =>
    {
        var random = new Random(922); var algorithm = catalog.Get<IDiffAlgorithm>("myers"); var plugin = catalog.Get<ITextPresentation>("side-by-side");
        for (int trial = 0; trial < 200; trial++)
        {
            var a = Enumerable.Range(0, random.Next(20)).Select(_ => "value=" + random.Next(6)).ToArray();
            var b = Enumerable.Range(0, random.Next(20)).Select(_ => "value=" + random.Next(6)).ToArray();
            var edits = algorithm.Compare(a, b, catalog).Edits;
            var lines = edits.Select(e => new DiffLine(e.Kind, e.LeftIndex + 1, e.RightIndex + 1, e.LeftIndex is int x ? a[x] : null, e.RightIndex is int y ? b[y] : null, "LF", "LF")).ToArray();
            var view = plugin.Build(lines);
            Check(view.Rows.Where(r => r.Left is not null).Select(r => r.Left!.Text).SequenceEqual(a), "Left sequence changed during display alignment.");
            Check(view.Rows.Where(r => r.Right is not null).Select(r => r.Right!.Text).SequenceEqual(b), "Right sequence changed during display alignment.");
        }
        return Task.CompletedTask;
    });
    await Test("Presentation handles EOL-only changes, empty input and cancellation", async () =>
    {
        var plugin = catalog.Get<ITextPresentation>("side-by-side");
        Check(plugin.Build([]).Rows.Count == 0, "Empty view contains rows.");
        var result = await Text("hello\n", "hello"); var view = plugin.Build(result.Files[0].Lines);
        Check(view.Hunks.Count == 1 && view.Rows.Count == 1 && view.Rows[0].Left!.Ending != view.Rows[0].Right!.Ending, "EOL-only difference lost.");
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Throws<OperationCanceledException>(() => plugin.Build(result.Files[0].Lines, cancellationToken: cts.Token));
    });
}
finally
{
    // All paths are generated under the dedicated temporary test root.
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    try { Directory.Delete(temp, true); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    { Console.WriteLine($"Temporary plugin files await process exit: {temp}"); }
}
Console.WriteLine($"\n{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

async Task Test(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + "\n" + ex); }
}
async Task<ComparisonResult> Text(string a, string b, ComparisonOptions? options = null, string algorithm = "myers")
{
    var left = Path.Combine(temp, "text-left"); var right = Path.Combine(temp, "text-right");
    await File.WriteAllTextAsync(left, a, new UTF8Encoding(false)); await File.WriteAllTextAsync(right, b, new UTF8Encoding(false));
    return await engine.CompareAsync(new(left, right, algorithm, options));
}
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}.");
}
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}.");
}
static void Validate(string[] left, string[] right, AlgorithmResult result)
{
    int x = 0, y = 0;
    foreach (var edit in result.Edits)
    {
        if (edit.Kind != EditKind.Insert) { Check(edit.LeftIndex == x, "Noncontiguous left index."); x++; }
        else Check(edit.LeftIndex is null, "Insert has left index.");
        if (edit.Kind != EditKind.Delete) { Check(edit.RightIndex == y, "Noncontiguous right index."); y++; }
        else Check(edit.RightIndex is null, "Delete has right index.");
        if (edit.Kind == EditKind.Equal) Check(left[x - 1] == right[y - 1], "Equal edit joins unequal lines.");
    }
    Check(x == left.Length && y == right.Length, "Edit script loses input lines.");
}
static int Distance(string[] a, string[] b)
{
    var lcs = new int[a.Length + 1, b.Length + 1];
    for (int i = 1; i <= a.Length; i++)
        for (int j = 1; j <= b.Length; j++)
            lcs[i, j] = a[i - 1] == b[j - 1] ? lcs[i - 1, j - 1] + 1 : Math.Max(lcs[i - 1, j], lcs[i, j - 1]);
    return a.Length + b.Length - 2 * lcs[a.Length, b.Length];
}
static void CopyDirectory(string source, string target)
{
    Directory.CreateDirectory(target);
    foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
    foreach (var directory in Directory.GetDirectories(source)) CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
}
