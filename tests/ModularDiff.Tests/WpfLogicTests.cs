using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using ModularDiff.Contracts;
using ModularDiff.Core;
using static ModularDiff.Contracts.FileStatus;

/// <summary>
/// The logic of the Windows (WPF) GUI that needs no window: the folder tree its folder view shows,
/// and its <c>Lang</c>. Both are loaded from the built GUI assembly, so they are tested wherever
/// that assembly can be built (with <c>EnableWindowsTargeting</c> also on macOS and Linux) and not
/// only by the smoke runs on Windows. The row lists themselves are WPF controls; only those runs
/// cover them.
/// </summary>
internal static class WpfLogicTests
{
    private static Type? _tree, _node, _lang;

    public static async Task Run(ComparisonEngine engine, string temp, Func<string, Func<Task>, Task> test)
    {
        string repo = DesktopGuardTests.RepoRoot();
        string sourceDirectory = Path.Combine(repo, "windows", "ModularDiff.Gui");
        var candidates = new List<string>();
        string published = Path.Combine(repo, "artifacts", "gui", "ModularDiff.Gui.dll");   // where build.ps1 puts it
        if (File.Exists(published)) candidates.Add(published);
        string bin = Path.Combine(sourceDirectory, "bin");                                   // where a project build puts it
        if (Directory.Exists(bin)) candidates.AddRange(Directory.EnumerateFiles(bin, "ModularDiff.Gui.dll", SearchOption.AllDirectories));
        if (candidates.Count == 0)
        {
            Console.WriteLine("SKIP  WPF GUI logic: the GUI is not built (dotnet build windows/ModularDiff.Gui/ModularDiff.Gui.csproj -p:EnableWindowsTargeting=true)");
            return;
        }
        string assembly = candidates.OrderByDescending(File.GetLastWriteTimeUtc).First();

        await test("WPF GUI logic: the built GUI assembly loads, and is not older than its sources", () =>
        {
            // An assembly from before the last edit would test code that no longer exists.
            string? newest = Directory.EnumerateFiles(sourceDirectory, "*.*", SearchOption.AllDirectories)
                .Where(f => Path.GetExtension(f) is ".cs" or ".xaml" && !InBinOrObj(f))
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            Check(newest is not null && File.GetLastWriteTimeUtc(newest) <= File.GetLastWriteTimeUtc(assembly),
                $"The GUI assembly is older than {newest}: rebuild it before trusting these tests.");
            Assembly gui = Assembly.LoadFrom(assembly);
            _tree = gui.GetType("ModularDiff.Gui.FolderTree", throwOnError: true);
            _node = gui.GetType("ModularDiff.Gui.FolderNode", throwOnError: true);
            _lang = gui.GetType("ModularDiff.Gui.Controls.Lang", throwOnError: true);
            return Task.CompletedTask;
        });
        if (_tree is null || _node is null || _lang is null) return;

        // Nothing is on disk under these roots: a size read fails the way it does for a file deleted since the scan.
        string left = Path.Combine(Path.GetTempPath(), "modulardiff-wpf-logic", "left"), right = Path.Combine(Path.GetTempPath(), "modulardiff-wpf-logic", "right");
        FolderNameFilter none = FolderNameFilter.Create("", regexMode: false);

        await test("WPF FolderTree: a final result gives a tree without a pending node; folders roll their subtree up, one-side folders keep their side", () =>
        {
            Tree tree = Tree.Build(Result(left, right,
                Folder("alpha", Modified), Entry("alpha/same.txt", Equal), Entry("alpha/diff.txt", Modified),
                Folder("beta", Equal), Entry("beta/one.txt", Equal), Entry("beta/two.txt", Equal),
                Folder("gone", LeftOnly), Entry("gone/old.txt", LeftOnly),
                Entry("top.txt", RightOnly)));

            Check(tree.All.Count == 9, $"9 nodes expected, the tree holds {tree.All.Count}.");
            Check(tree.All.All(node => node.Status != FileStatus.Pending), "a tree of a final result still shows a pending node");
            Check(Paths(tree.Roots) == Seq("alpha", "beta", "gone", "top.txt"), "folders come first, in name order: " + Paths(tree.Roots));
            var expected = new (string Path, FileStatus Status, bool Directory)[]
            {
                ("alpha", Modified, true), (At("alpha", "diff.txt"), Modified, false), (At("alpha", "same.txt"), Equal, false),
                ("beta", Equal, true), (At("beta", "one.txt"), Equal, false), (At("beta", "two.txt"), Equal, false),
                ("gone", LeftOnly, true), (At("gone", "old.txt"), LeftOnly, false), ("top.txt", RightOnly, false),
            };
            foreach (var (path, status, directory) in expected)
            {
                Node node = tree[path];
                Check(node.Status == status && node.IsDirectory == directory, $"'{path}' should be {status} (directory: {directory}), is {node.Status} (directory: {node.IsDirectory}).");
            }
            Check(tree["gone"].LeftExists && !tree["gone"].RightExists && !tree["top.txt"].LeftExists && tree["top.txt"].RightExists, "a one-side node exists on its side only");
            Check(Paths(tree["alpha"].Children) == Seq(At("alpha", "diff.txt"), At("alpha", "same.txt")) && tree["alpha"].Children.Count == 2, "a folder lists its children in name order");
            Check(ReferenceEquals(tree[At("alpha", "diff.txt")].Parent?.Target, tree["alpha"].Target) && tree["top.txt"].Parent is null, "a node knows its folder; a root has none");
            return Task.CompletedTask;
        });

        await test("WPF FolderTree: a folder that could not be read is shown as an error, not as merely different", () =>
        {
            Tree tree = Tree.Build(Result(left, right,
                Folder("broken", Pending), Entry("broken/locked.txt", Error, "left: The process cannot access the file."),
                // The scan listed 'secret' on both sides, then could not open the left one. What replaces the folder in the
                // inventory is the error itself, and the right side's files are listed under it.
                Entry("secret", Error, "left: Access to the path is denied."), Entry("secret/keep.txt", RightOnly),
                Entry("vault", Error, "right: Access to the path is denied."), Entry("clean.txt", Equal)));

            Node secret = tree["secret"];
            Check(secret.IsDirectory, "'secret' has a file under it, so its node is the folder");
            // "Different" suggests files that merely have to be copied; the truth is that one side could not be looked at.
            Check(secret.Status == FileStatus.Error && secret.StatusGlyph == "!" && secret.Color == "#B47A14",
                $"an unreadable folder must be an error row (is {secret.Status}, glyph '{secret.StatusGlyph}')");
            Check(tree["broken"].Status == FileStatus.Error, "a folder with an error inside is an error");
            Node vault = tree["vault"];
            Check(!vault.IsDirectory && vault.Status == FileStatus.Error, "a folder nobody could list is known only by its error entry");
            Check(Paths(tree.Visible(1, none)) == Seq("broken", "secret", "vault"), "'different' must list the unreadable folders: " + Paths(tree.Visible(1, none)));
            tree["secret"].IsExpanded = true;
            Check(Paths(tree.Visible(1, none)) == Seq("broken", "secret", At("secret", "keep.txt"), "vault"), "and, once opened, what could be read under them: " + Paths(tree.Visible(1, none)));
            Check(Paths(tree.Visible(2, none)) == "clean.txt", "'same' must not list an unreadable folder: " + Paths(tree.Visible(2, none)));
            return Task.CompletedTask;
        });

        await test("WPF FolderNode: a node announces what a row shows, so a row that keeps its node is redrawn when the node changes under it", () =>
        {
            Tree tree = Tree.Build(Result(left, right,
                Folder("alpha", Modified), Entry("alpha/same.txt", Equal), Entry("alpha/diff.txt", Modified),
                Folder("beta", Equal), Entry("beta/one.txt", Equal), Entry("top.txt", RightOnly)));
            Node alpha = tree["alpha"], beta = tree["beta"], diff = tree[At("alpha", "diff.txt")];
            var raised = new List<string?>();
            // The pooled row lists keep a row's visuals and its data context while the node stays at that position. Without
            // an announcement the row goes on showing the old state: a folder's arrow that does not turn when it is
            // expanded, a file that is still '…' after it has been compared.
            if (alpha.Target is not INotifyPropertyChanged observable)
                throw new Exception("FolderNode does not implement INotifyPropertyChanged.");
            observable.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            INotifyPropertyChanged On(Node node) => (INotifyPropertyChanged)node.Target;

            // Exactly the names a row binds to that changed: none missing (the row would keep the old text), none extra
            // (each costs a redraw), and none when the value did not change.
            void Change(Action change, params string[] names)
            {
                raised.Clear();
                change();
                string[] unexpected = raised.Where(name => !names.Contains(name!)).ToArray()!;
                string[] missing = names.Where(name => !raised.Contains(name)).ToArray();
                Check(missing.Length == 0 && unexpected.Length == 0, $"expected exactly [{string.Join(", ", names)}] to be announced, got [{string.Join(", ", raised)}]");
            }

            Change(() => alpha.IsExpanded = true, "Symbol");
            Change(() => alpha.IsExpanded = true);                               // nothing changed: nothing to redraw
            Change(() => alpha.IsExpanded = false, "Symbol");
            Change(() => alpha.Status = FileStatus.Error, "Status", "StatusGlyph", "StatusText", "Color", "Description");
            Change(() => alpha.Status = FileStatus.Error);
            Change(() => alpha.LeftExists = false, "LeftExists", "LeftName");
            Change(() => alpha.RightExists = false, "RightExists", "RightName");
            Change(() => alpha.File = Entry("alpha", Error, "left: Access to the path is denied."), "Description");
            Change(() => alpha.LeftSize = "1 KB", "LeftSize");
            Change(() => alpha.LeftModified = "2026-01-01 10:00", "LeftModified");
            Change(() => alpha.RightSize = "2 KB", "RightSize");
            Change(() => alpha.RightModified = "2026-01-02 10:00", "RightModified");

            // The same through the tree, the way the window patches it while a scan runs.
            On(beta).PropertyChanged += (_, e) => raised.Add("beta." + e.PropertyName);
            On(diff).PropertyChanged += (_, e) => raised.Add("diff." + e.PropertyName);
            raised.Clear();
            Check(tree.ApplyFileUpdate(Entry(At("alpha", "diff.txt"), Error, "left: locked"), left, right), "the file is in the tree");
            Check(raised.Contains("diff.Status") && raised.Contains("diff.StatusGlyph") && raised.Contains("Status") && !raised.Contains("beta.Status"),
                $"a resolved file and the folders above it are redrawn, others are left alone (told: {string.Join(", ", raised)})");
            raised.Clear();
            Check(tree.MergeAdded([Entry(At("beta", "extra.txt"), RightOnly)], left, right) == 1 && raised.Contains("beta.Status") && !raised.Contains("diff."),
                $"a file found under 'beta' makes 'beta' different; rows that did not change are left alone (told: {string.Join(", ", raised)})");
            return Task.CompletedTask;
        });

        await test("WPF FolderTree: fed the scan reports the way the window feeds it, the tree ends where a tree of the final result does", async () =>
        {
            string root = Path.Combine(temp, "wpf-scan"), l = Path.Combine(root, "L"), r = Path.Combine(root, "R");
            void Put(string side, string relative, string text) { string path = Path.Combine(side, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
            Put(l, "same.txt", "a"); Put(r, "same.txt", "a");
            Put(l, "diff.txt", "x"); Put(r, "diff.txt", "y");
            Put(l, Path.Combine("clean", "x.txt"), "1"); Put(r, Path.Combine("clean", "x.txt"), "1");
            Put(l, Path.Combine("dirty", "y.txt"), "1"); Put(r, Path.Combine("dirty", "y.txt"), "2");
            Put(l, Path.Combine("onlyL", "z.txt"), "z"); Put(r, Path.Combine("onlyR", "w.txt"), "w");
            Put(l, Path.Combine("deep", "a", "b", "c", "n.txt"), "L"); Put(r, Path.Combine("deep", "a", "b", "c", "n.txt"), "R");
            Put(l, Path.Combine("broken", "locked.txt"), "1"); Put(r, Path.Combine("broken", "locked.txt"), "1");
            Directory.CreateDirectory(Path.Combine(l, "empty")); Directory.CreateDirectory(Path.Combine(r, "empty"));
            Directory.CreateDirectory(Path.Combine(l, "locked")); Put(r, Path.Combine("locked", "inside.txt"), "R");
            string lockedDirectory = Path.Combine(l, "locked");

            var recorder = new DirectoryScanTests.RecordingProgress();
            ComparisonResult final;
            try
            {
                SetMode(lockedDirectory, 0);   // the owner cannot list it either
                // The left copy of broken/locked.txt is held open exclusively: comparing it ends in an error entry (a sharing
                // violation on Windows, a lock .NET emulates on Unix).
                using (var held = new FileStream(Path.Combine(l, "broken", "locked.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    final = await engine.CompareAsync(new(l, r, "myers"), progress: recorder);
            }
            finally { SetMode(lockedDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            Check(final.HasErrors && final.Files.Any(f => f.RelativePath == "locked" && f.Status == FileStatus.Error) == !OperatingSystem.IsWindows(),
                "the scenario must produce the errors it was made for");
            Check(recorder.RootWave is not null && recorder.RootWave.Files.Any(f => f.Status == FileStatus.Pending) && recorder.Added.Count > 0 && recorder.Updates.Count >= 6,
                $"the scan must have run in waves (root wave {recorder.RootWave?.Files.Count} entries, {recorder.Added.Count} announced later, {recorder.Updates.Count} resolved)");

            // As MainWindow does: the root wave builds the tree, every later report is applied to it in arrival order.
            Tree replayed = null!;
            foreach (var report in recorder.Reports)
            {
                if (report.PartialResult is { } partial) replayed = Tree.Build(partial);
                if (report.Added is { } added) replayed.MergeAdded(added, l, r);
                if (report.FileUpdate is { } update) replayed.ApplyFileUpdate(update, l, r);
            }
            Tree built = Tree.Build(final);
            string[] paths = [.. replayed.All.Select(node => node.RelativePath).Union(built.All.Select(node => node.RelativePath)).Order(StringComparer.Ordinal)];
            string[] missing = paths.Where(path => replayed.Find(path) is null || built.Find(path) is null).ToArray();
            Check(missing.Length == 0, $"the replayed tree lists {replayed.All.Count} nodes, the final one {built.All.Count}; only on one side: {string.Join(", ", missing.Take(8))}");
            // The list each tree keeps is in the order its nodes were created (waves for one, the sorted inventory for the
            // other), so nodes are compared by path.
            string[] differences = paths.Where(path => !Same(replayed[path], built[path]))
                .Select(path => $"{path}: {Show(replayed[path])} during the scan, {Show(built[path])} at the end").ToArray();
            Check(differences.Length == 0, "the tree shown while the scan ran must be the tree of the final result:\n  " + string.Join("\n  ", differences.Take(10)));
            static void SetMode(string directory, UnixFileMode mode)
            {
                if (OperatingSystem.IsWindows() || !Directory.Exists(directory)) return;   // Windows guards a folder with ACLs, not modes
                new DirectoryInfo(directory).UnixFileMode = mode;
            }
            static bool Same(Node a, Node b) => a.Status == b.Status && a.IsDirectory == b.IsDirectory && a.LeftExists == b.LeftExists && a.RightExists == b.RightExists;
            static string Show(Node node) => $"{node.Status} (folder: {node.IsDirectory}, left: {node.LeftExists}, right: {node.RightExists})";
        });

        await test("WPF FolderTree: the rows come from the status filter, the name filter and the expand state, as the folder view promises", () =>
        {
            Tree tree = Tree.Build(Result(left, right,
                Folder("alpha", Modified), Entry("alpha/diff.txt", Modified), Entry("alpha/same.txt", Equal),
                Folder("beta", Equal), Entry("beta/one.txt", Equal), Entry("beta/two.txt", Equal),
                Folder("gone", LeftOnly), Entry("gone/old.txt", LeftOnly),
                Entry("top.txt", RightOnly)));
            string[] Rows(int filter, string query = "", bool regex = false) => Paths(tree.Visible(filter, FolderNameFilter.Create(query, regex))).Split('\n');
            string All() => Seq("alpha", At("alpha", "diff.txt"), At("alpha", "same.txt"), "beta", At("beta", "one.txt"), At("beta", "two.txt"), "gone", At("gone", "old.txt"), "top.txt");

            Check(Seq(Rows(0)) == Seq("alpha", "beta", "gone", "top.txt"), "collapsed folders list no children: " + Seq(Rows(0)));
            tree["alpha"].IsExpanded = true;
            Check(Seq(Rows(0)) == Seq("alpha", At("alpha", "diff.txt"), At("alpha", "same.txt"), "beta", "gone", "top.txt"), "an expanded folder lists its children after it");
            Check(Seq(Rows(2)) == Seq("alpha", At("alpha", "same.txt"), "beta"), "'same' lists the equal rows and the folders that lead to them: " + Seq(Rows(2)));
            Check(Seq(Rows(1)) == Seq("alpha", At("alpha", "diff.txt"), "gone", "top.txt"), "'different' lists the differing rows: " + Seq(Rows(1)));
            Check(Seq(Rows(3)) == "gone" && Seq(Rows(4)) == "top.txt", "a side filter lists what exists on that side only");
            tree["gone"].IsExpanded = true;
            Check(Seq(Rows(3)) == Seq("gone", At("gone", "old.txt")), "expanding a one-side folder lists its files: " + Seq(Rows(3)));
            Check(Seq(Rows(0, "same")) == Seq("alpha", At("alpha", "same.txt")), "a name filter opens every folder and keeps the way down to a match: " + Seq(Rows(0, "same")));
            Check(Seq(Rows(0, "TXT")) == All(), "a name filter ignores case: " + Seq(Rows(0, "TXT")));
            Check(Seq(Rows(0, "t?o.txt")) == Seq("beta", At("beta", "two.txt")), "globs: " + Seq(Rows(0, "t?o.txt")));
            Check(Seq(Rows(0, @"^beta/.*\.txt$", regex: true)) == Seq("beta", At("beta", "one.txt"), At("beta", "two.txt")), "regular expressions: " + Seq(Rows(0, @"^beta/.*\.txt$", regex: true)));
            Check(Seq(Rows(2, "txt")) == Seq("alpha", At("alpha", "same.txt"), "beta", At("beta", "one.txt"), At("beta", "two.txt")), "status and name filters combine: " + Seq(Rows(2, "txt")));
            Check(Rows(0, "nothing-here").Length == 1 && Rows(0, "nothing-here")[0] == "", "a name nothing matches lists nothing");
            Check(FolderNameFilter.Create("(", regexMode: true).Error is not null && tree.Visible(0, FolderNameFilter.Create("(", true)).Count == 0, "an invalid expression matches nothing and says why");
            Check(ReferenceEquals(tree.VisibleList(0, none), tree.VisibleList(0, none)), "the row list is reused between keystrokes");
            return Task.CompletedTask;
        });

        await test("WPF Lang: the packs load with the same keys, and a message the active pack lacks is shown in English without English having been applied first", () =>
        {
            // Lang looked for its packs next to this executable, where there are none: load the sources instead.
            Type lang = _lang;
            MethodInfo load = lang.GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Static) ?? throw new Exception("Lang.Load is gone.");
            MethodInfo validateParity = lang.GetMethod("ValidateKeyParity", BindingFlags.NonPublic | BindingFlags.Static) ?? throw new Exception("Lang.ValidateKeyParity is gone.");
            foreach (string pack in Directory.EnumerateFiles(Path.Combine(sourceDirectory, "languages"), "lang-*.json").Order(StringComparer.Ordinal))
                load.Invoke(null, [pack]);
            validateParity.Invoke(null, null);
            string Text(string source) => (string)lang.GetMethod("Text", [typeof(string)])!.Invoke(null, [source])!;
            string Summary(object[] args) => (string)lang.GetMethod("Get", [typeof(string), typeof(object[])])!.Invoke(null, ["status_summary", args])!;
            string Key(string key) => (string)lang.GetMethod("Get", [typeof(string)])!.Invoke(null, [key])!;
            void Apply(string name) => lang.GetMethod("Apply", [typeof(string)])!.Invoke(null, [name]);
            string[] Names() => ((IEnumerable)lang.GetProperty("Available")!.GetValue(null)!).Cast<string>().ToArray();
            string[] Diagnostics() => ((IEnumerable)lang.GetProperty("LoadDiagnostics")!.GetValue(null)!).Cast<string>().ToArray();

            string[] names = Names();
            Check(names.Length == 5, $"five packs expected, Lang knows {names.Length}: {string.Join(", ", names)}");
            Check(Diagnostics().Length == 0, "language pack diagnostics:\n  " + string.Join("\n  ", Diagnostics()));
            try
            {
                // As a session that never chose English: no Apply("English") may come before the fallback check below.
                Apply("中文");
                Check(Text("Choose two existing files or two existing directories.") == "请选择两个存在的文件或两个存在的目录。", $"Chinese must translate the engine's message (got '{Text("Choose two existing files or two existing directories.")}')");
                Apply("日本語");
                Check(Text("内容比较中…") == "内容を比較中…", $"Japanese must translate the plugin's note (got '{Text("内容比较中…")}')");

                // A pack that translates nothing of the engine's and plugins' messages, as a new language does at first.
                string pack = Path.Combine(temp, "lang-test-fallback.json");
                File.WriteAllText(pack, JsonSerializer.Serialize(new { name = "TestFallback", code = "xx-XX", strings = new Dictionary<string, string> { ["status_summary"] = "{0} files" }, messages = new Dictionary<string, string>() }));
                load.Invoke(null, [pack]);
                Apply("TestFallback");
                Check(Text("内容比较中…") == "Comparing content…",
                    $"a message this pack does not carry must be shown in English, not in the source language (got '{Text("内容比较中…")}')");
                Check(Key("folder_scan_root") == LangKey("English", "folder_scan_root"), "an interface text this pack does not carry must be shown in English");
                Check(Summary([7, 2, 5, 0]) == "7 files", "a text this pack does carry is its own");

                foreach (string name in names)
                {
                    Apply(name);
                    Check(!Summary([7, 2, 5, 0]).Contains("status_summary") && Summary([7, 2, 5, 0]).Contains("7"), $"{name}: 'status_summary' does not format");
                }
                Apply("English");
                Check(Text("内容比较中…") == "Comparing content…", $"English must translate the plugin's note (got '{Text("内容比较中…")}')");
            }
            finally { Apply("中文"); }
            return Task.CompletedTask;

            string LangKey(string pack, string key) { Apply(pack); string text = Key(key); Apply("TestFallback"); return text; }
        });
    }

    private static FileComparison Entry(string path, FileStatus status, string? note = null) => new(path, status, [], Note: note);
    private static FileComparison Folder(string path, FileStatus status) => new(path, status, [], IsDirectory: true);
    private static ComparisonResult Result(string left, string right, params FileComparison[] files) => new(left, right, "directories", "myers", files, []);
    private static string Paths(IEnumerable<Node> nodes) => string.Join("\n", nodes.Select(node => node.RelativePath));
    /// <summary>Row lists and node lists are compared as text, one path per line.</summary>
    private static string Seq(params string[] parts) => string.Join("\n", parts);
    private static string At(params string[] parts) => Path.Combine(parts);
    private static bool InBinOrObj(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj");

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    /// <summary>A <c>FolderNode</c> seen through reflection: the members a row binds to, and never <c>Indent</c>
    /// (a WPF <c>Thickness</c>), which is what keeps this runnable off Windows.</summary>
    private sealed class Node(object node)
    {
        private readonly Type _type = node.GetType();
        public object Target { get; } = node;
        private object? Get(string name) => _type.GetProperty(name)!.GetValue(Target);
        private void Set(string name, object? value) => _type.GetProperty(name)!.SetValue(Target, value);

        public string RelativePath => (string)Get("RelativePath")!;
        public bool IsDirectory { get => (bool)Get("IsDirectory")!; set => Set("IsDirectory", value); }
        public bool IsExpanded { get => (bool)Get("IsExpanded")!; set => Set("IsExpanded", value); }
        public bool LeftExists { get => (bool)Get("LeftExists")!; set => Set("LeftExists", value); }
        public bool RightExists { get => (bool)Get("RightExists")!; set => Set("RightExists", value); }
        public FileComparison? File { get => (FileComparison?)Get("File"); set => Set("File", value); }
        public FileStatus Status { get => (FileStatus)Get("Status")!; set => Set("Status", value); }
        public string StatusGlyph => (string)Get("StatusGlyph")!;
        public string Color => (string)Get("Color")!;
        public string LeftSize { get => (string)Get("LeftSize")!; set => Set("LeftSize", value); }
        public string RightSize { get => (string)Get("RightSize")!; set => Set("RightSize", value); }
        public string LeftModified { get => (string)Get("LeftModified")!; set => Set("LeftModified", value); }
        public string RightModified { get => (string)Get("RightModified")!; set => Set("RightModified", value); }
        public Node? Parent => Get("Parent") is { } parent ? new Node(parent) : null;
        public IReadOnlyList<Node> Children => ((IEnumerable)Get("Children")!).Cast<object>().Select(child => new Node(child)).ToArray();
    }

    private sealed class Tree(object tree)
    {
        private readonly Type _type = tree.GetType();
        public object Target { get; } = tree;
        private IReadOnlyList<Node> Nodes(string name) => ((IEnumerable)_type.GetProperty(name)!.GetValue(Target)!).Cast<object>().Select(node => new Node(node)).ToArray();

        public static Tree Build(ComparisonResult result) => new(_tree!.GetMethod("Build")!.Invoke(null, [result, CancellationToken.None])!);
        public IReadOnlyList<Node> Roots => Nodes("Roots");
        public IReadOnlyList<Node> All => Nodes("All");
        /// <summary>The node at <paramref name="path"/>, or null when the tree has none.</summary>
        public Node? Find(string path) => All.FirstOrDefault(node => node.RelativePath == path);
        public Node this[string path] => Find(path) ?? throw new Exception($"The tree has no node for '{path}'.");
        public int MergeAdded(IReadOnlyList<FileComparison> added, string leftRoot, string rightRoot) =>
            (int)_type.GetMethod("MergeAdded")!.Invoke(Target, [added, leftRoot, rightRoot, CancellationToken.None])!;
        public bool ApplyFileUpdate(FileComparison file, string leftRoot, string rightRoot) =>
            (bool)_type.GetMethod("ApplyFileUpdate")!.Invoke(Target, [file, leftRoot, rightRoot])!;
        public object VisibleList(int filter, FolderNameFilter nameFilter) => _type.GetMethod("Visible")!.Invoke(Target, [filter, nameFilter])!;
        public IReadOnlyList<Node> Visible(int filter, FolderNameFilter nameFilter) =>
            ((IEnumerable)VisibleList(filter, nameFilter)!).Cast<object>().Select(node => new Node(node)).ToArray();
    }
}
