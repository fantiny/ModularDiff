namespace ModularDiff.Core;

/// <summary>用户数据的原子写:先写同目录临时文件,再 File.Replace 落位。进程崩溃/断电不会留下半截文件,
/// 也不会让并发读者看到截断的 JSON(2026-11 审查:SessionStore/Settings/MERGED 均为裸 WriteAllText)。
/// 凡是"丢了无法重建"的写入都必须走这里;可重建的临时产物(报告、补丁)不需要。</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string? contents)
    {
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, contents ?? "");
            Replace(temp, path);
        }
        finally { TryDelete(temp); }
    }

    public static void WriteAllBytes(string path, byte[] contents)
    {
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temp, contents);
            Replace(temp, path);
        }
        finally { TryDelete(temp); }
    }

    private static void Replace(string temp, string path)
    {
        if (File.Exists(path)) File.Replace(temp, path, destinationBackupFileName: null);
        else File.Move(temp, path);
    }

    private static void TryDelete(string temp)
    {
        try { if (File.Exists(temp)) File.Delete(temp); } catch { /* 替换失败时清理;清理失败无害 */ }
    }
}
