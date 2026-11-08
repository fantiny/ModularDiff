using System.Security.Cryptography;
using ModularDiff.Contracts;

namespace ModularDiff.Sync;

internal static class SyncFiles
{
    public static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static (string Left, string Right) Roots(string left, string right)
    {
        left = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)); right = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        if (Comparer.Equals(left, right) || Inside(left, right) || Inside(right, left)) throw new IOException("同步根目录不能相同或互相包含。");
        foreach (string root in new[] { left, right }) { NoLinks(root); if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root); }
        return (left, right);
    }
    private static bool Inside(string root, string path) => path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, Comparison);
    public static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)
            || relative.Split(['/', '\\']).Any(p => p is ".." or "." or "" || p.Contains(':') || p.EndsWith(' ') || p.EndsWith('.')))
            throw new IOException("无效的相对路径：" + relative);
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!Inside(root, full)) throw new IOException("路径超出同步根目录。");
        NoLinks(full); return full;
    }
    public static void NoLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("不支持符号链接或目录联接：" + current);
                if (current != path && (attributes & FileAttributes.Directory) == 0) throw new IOException("父路径不是目录：" + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    public static async Task<SyncStamp> Stamp(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); NoLinks(path);
        if (Directory.Exists(path)) throw new IOException("文件路径与目录冲突：" + path);
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return new(true, file.Length, Convert.ToHexString(await SHA256.HashDataAsync(file, token)), File.GetLastWriteTimeUtc(path).Ticks);
        }
        catch (FileNotFoundException) { return new(false); }
        catch (DirectoryNotFoundException) { return new(false); }
    }
    public static bool SameContent(SyncStamp a, SyncStamp b) => a.Exists && b.Exists && a.Length == b.Length && a.Hash == b.Hash;
    public static async Task Validate(SyncPlan plan, SyncItem item, CancellationToken token)
    {
        Roots(plan.LeftRoot, plan.RightRoot);
        if (await Stamp(Resolve(plan.LeftRoot, item.RelativePath), token) != item.Left || await Stamp(Resolve(plan.RightRoot, item.RelativePath), token) != item.Right)
            throw new IOException("预览后文件已变化，请重新生成同步预览。");
    }
}
