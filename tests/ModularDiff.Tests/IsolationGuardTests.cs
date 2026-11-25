using ModularDiff.Core;

/// <summary>测试隔离守卫:应用的全部用户数据必须经由可重定向的 AppPaths 落盘。
/// 背景:UI 测试套件曾把临时路径写进用户真实的「最近比较」列表、并覆盖用户的界面语言偏好 ——
/// 这类泄漏不会有任何测试失败,只会悄悄损坏用户数据,所以用静态守卫从构造上排除。</summary>
internal static class IsolationGuardTests
{
    public static async Task Run(string temp, Func<string, Func<Task>, Task> test)
    {
        await test("AppPaths: override redirects every persisted location; default is the per-user directory", () =>
        {
            string? previous = Environment.GetEnvironmentVariable(AppPaths.OverrideVariable);
            try
            {
                Environment.SetEnvironmentVariable(AppPaths.OverrideVariable, null);
                string userDirectory = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModularDiff"));
                Check(AppPaths.DataDirectory == userDirectory, $"default directory should be {userDirectory}, got {AppPaths.DataDirectory}");
                Check(SessionStore.DefaultPath == Path.Combine(userDirectory, "Sessions.json"), "SessionStore must resolve inside the data directory");

                string over = Path.Combine(temp, "redirected-data");
                Environment.SetEnvironmentVariable(AppPaths.OverrideVariable, "  " + over + "  ");   // 容忍首尾空白
                Check(AppPaths.DataDirectory == Path.GetFullPath(over), $"override not honoured: {AppPaths.DataDirectory}");
                Check(SessionStore.DefaultPath == Path.Combine(Path.GetFullPath(over), "Sessions.json"), "SessionStore ignored the override");
                Check(!Directory.Exists(userDirectory) || !File.Exists(Path.Combine(userDirectory, "Sessions.json"))
                    || new FileInfo(Path.Combine(userDirectory, "Sessions.json")).LastWriteTimeUtc <= DateTime.UtcNow.AddSeconds(-5),
                    "precondition: this test must not be preceded by a write to the real sessions file");

                // 经默认入口的一次完整读写往返,只能落在被重定向的目录里。
                var stored = SessionStore.Load();
                SessionStore.Save(stored.RecordRecent(Path.Combine(temp, "a.txt"), Path.Combine(temp, "b.txt"), "myers", false, false, false, true));
                Check(File.Exists(Path.Combine(over, "Sessions.json")), "round trip did not write into the redirected directory");
                Check(SessionStore.Load().Recent.Count == 1 && SessionStore.Load().Recent[0].Left.EndsWith("a.txt", StringComparison.Ordinal), "round trip lost data");
            }
            finally { Environment.SetEnvironmentVariable(AppPaths.OverrideVariable, previous); }
            return Task.CompletedTask;
        });

        await test("Isolation guard: nothing in Core or the Desktop GUI writes user data outside AppPaths", () =>
        {
            string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "shared/ModularDiff.Core/AppPaths.cs",                       // the definition itself
                "cross-platform/ModularDiff.Desktop/Program.cs",            // IPC socket only: not user data, shared with the Finder helper
            };
            var offenders = new List<string>();
            foreach (string root in new[] { Path.Combine(repo, "shared", "ModularDiff.Core"), Path.Combine(repo, "cross-platform", "ModularDiff.Desktop") })
                foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
                {
                    string name = Path.GetRelativePath(repo, file).Replace('\\', '/');
                    if (name.Split('/').Any(part => part is "bin" or "obj") || allowed.Contains(name)) continue;
                    string[] lines = File.ReadAllLines(file);
                    for (int i = 0; i < lines.Length; i++)
                        if (lines[i].Contains("SpecialFolder.LocalApplicationData", StringComparison.Ordinal) || lines[i].Contains("SpecialFolder.ApplicationData", StringComparison.Ordinal))
                            offenders.Add($"{name}:{i + 1}");
                }
            Check(offenders.Count == 0, "User-data path computed directly (bypasses the test override — use AppPaths.DataDirectory):\n  " + string.Join("\n  ", offenders));
            return Task.CompletedTask;
        });
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
