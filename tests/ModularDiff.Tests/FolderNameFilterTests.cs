using ModularDiff.Core;

internal static class FolderNameFilterTests
{
    public static Task Run(Func<string, Func<Task>, Task> test) => test("FolderNameFilter: substring, glob, regex, path-or-name", () =>
    {
        var empty = FolderNameFilter.Create("", regexMode: false);
        Check(empty.IsEmpty && empty.Error is null && empty.Matches(@"src\a.cs", "a.cs"), "empty matches all");

        var sub = FolderNameFilter.Create("src", regexMode: false);
        Check(sub.Matches(@"src\a.cs", "a.cs"), "substring path");
        Check(sub.Matches(@"lib\helper.cs", "srcHelper.cs"), "substring name");
        Check(!sub.Matches(@"lib\a.cs", "a.cs"), "substring miss");

        var cs = FolderNameFilter.Create("*.cs", regexMode: false);
        Check(cs.Matches(@"src\Foo.cs", "Foo.cs"), "glob *.cs path");
        Check(cs.Matches(@"Foo.cs", "Foo.cs"), "glob *.cs root");
        Check(!cs.Matches(@"src\Foo.csproj", "Foo.csproj"), "glob *.cs excludes csproj");

        var models = FolderNameFilter.Create("**/Models/**", regexMode: false);
        Check(models.Matches(@"app/Models/Foo.cs", "Foo.cs"), "glob **/Models/**");
        Check(models.Matches(@"Models\Bar.cs", "Bar.cs"), "glob Models at root");
        Check(!models.Matches(@"app/Model/Foo.cs", "Foo.cs"), "glob Models miss");

        var nested = FolderNameFilter.Create("src/*.json", regexMode: false);
        Check(nested.Matches(@"src\a.json", "a.json"), "glob src/*.json");
        Check(!nested.Matches(@"src\deep\a.json", "a.json"), "glob src/*.json no deep");

        var re = FolderNameFilter.Create(@"\.json$", regexMode: true);
        Check(re.Error is null && re.Matches(@"x\a.json", "a.json"), "regex .json$");
        Check(!re.Matches(@"x\a.json.bak", "a.json.bak"), "regex rejects .json.bak");

        var bad = FolderNameFilter.Create("(", regexMode: true);
        Check(bad.Error is not null && !bad.Matches("a", "a"), "invalid regex no match + error");

        Check(FolderNameFilter.Create("*.CS", false).Matches(@"a.cs", "a.cs"), "glob ignore case");
        Check(FolderNameFilter.Create("FOO", false).Matches(@"foo\bar", "bar"), "substring ignore case");
        return Task.CompletedTask;
    });

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
