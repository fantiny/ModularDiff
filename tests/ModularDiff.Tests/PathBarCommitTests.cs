using ModularDiff.Core;

internal static class PathBarCommitTests
{
    public static Task Run(Func<string, Func<Task>, Task> test) => test("PathBarCommit: exists + same kind, else error", () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "ModularDiff.PathBar-" + Guid.NewGuid().ToString("N"));
        string leftDir = Path.Combine(root, "left");
        string rightDir = Path.Combine(root, "right");
        string leftFile = Path.Combine(leftDir, "a.txt");
        string rightFile = Path.Combine(rightDir, "b.txt");
        Directory.CreateDirectory(leftDir);
        Directory.CreateDirectory(rightDir);
        File.WriteAllText(leftFile, "a");
        File.WriteAllText(rightFile, "b");
        try
        {
            Check(PathBarCommit.TryResolve(leftDir, rightDir, out var ld, out var rd, out var err)
                  && err is null && Directory.Exists(ld) && Directory.Exists(rd), "dir+dir");
            Check(PathBarCommit.TryResolve(leftFile, rightFile, out var lf, out var rf, out err)
                  && err is null && File.Exists(lf) && File.Exists(rf), "file+file");
            Check(!PathBarCommit.TryResolve(leftFile, rightDir, out _, out _, out err)
                  && err is not null, "file+dir rejected");
            Check(!PathBarCommit.TryResolve(leftDir, Path.Combine(root, "missing"), out _, out _, out err)
                  && err is not null, "missing rejected");
            Check(!PathBarCommit.TryResolve("", rightDir, out _, out _, out err)
                  && err is not null, "empty rejected");
            Check(!PathBarCommit.TryResolve(leftFile + "\0", rightFile, out _, out _, out err)
                  && err is not null, "invalid chars rejected");
        }
        finally { try { Directory.Delete(root, true); } catch { /* ignore */ } }
        return Task.CompletedTask;
    });

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
