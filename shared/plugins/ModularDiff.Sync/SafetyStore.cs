using System.Security.Cryptography;
using System.Text.Json;
using ModularDiff.Contracts;

namespace ModularDiff.Sync;

internal static class SafetyStore
{
    public static SyncSafetyContext Default() => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModularDiff", "SyncHistory"), new FileSyncBackup(), new FileSyncJournal());
    public static string Root(string root)
    {
        root = Path.GetFullPath(root); SyncFiles.NoLinks(root); return root;
    }
    public static void Outside(string store, string left, string right)
    {
        store = Path.TrimEndingDirectorySeparator(Root(store));
        foreach (var input in new[] { left, right })
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
            if (SyncFiles.Comparer.Equals(store, path) || store.StartsWith(Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(Path.EndsInDirectorySeparator(store) ? store : store + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("备份记录目录必须位于同步根目录之外。");
        }
    }
    public static string Asset(string root, string folder, string id, string extension)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new IOException("无效的备份或记录 ID。");
        string path = Path.Combine(Root(root), folder, id + extension); SyncFiles.NoLinks(path); return path;
    }
    public static async Task CopyFlushed(Stream input, string destination, CancellationToken token)
    {
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, token); await output.FlushAsync(token); output.Flush(true);
    }
}

public sealed class FileSyncBackup : ISyncBackup
{
    public string Id => "sync-backup";
    public string DisplayName => "Verified immutable file backup";
    public async Task<string> CaptureAsync(string storeRoot, string file, SyncStamp expected, CancellationToken cancellationToken = default)
    {
        if (!expected.Exists || await SyncFiles.Stamp(file, cancellationToken) != expected) throw new IOException("备份来源已变化，停止写入目标。");
        string id = Guid.NewGuid().ToString("N"), path = SafetyStore.Asset(storeRoot, "backups", id, ".bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); SyncFiles.NoLinks(path);
        try
        {
            await using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
                await SafetyStore.CopyFlushed(input, path, cancellationToken);
            if (!SyncFiles.SameContent(await SyncFiles.Stamp(path, cancellationToken), expected) || await SyncFiles.Stamp(file, cancellationToken) != expected)
                throw new IOException("备份校验失败，停止写入目标。");
            return id;
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }
    public async Task<Stream> OpenVerifiedAsync(string storeRoot, string backupId, SyncStamp expected, CancellationToken cancellationToken = default)
    {
        string path = SafetyStore.Asset(storeRoot, "backups", backupId, ".bin");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        try
        {
            if (stream.Length != expected.Length || Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)) != expected.Hash)
                throw new IOException("备份内容损坏，未恢复目标文件。");
            stream.Position = 0; return stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }
}

public sealed class FileSyncJournal : ISyncJournal
{
    public string Id => "sync-journal";
    public string DisplayName => "Durable JSON operation journal";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public Task<IAsyncDisposable> AcquireAsync(string storeRoot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); string root = SafetyStore.Root(storeRoot); Directory.CreateDirectory(root);
        string path = Path.Combine(root, "operation.lock"); SyncFiles.NoLinks(path);
        try { return Task.FromResult<IAsyncDisposable>(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
        catch (IOException ex) { throw new IOException("同步记录库正在使用或无法访问，请稍后重试。", ex); }
    }
    public async Task SaveAsync(string storeRoot, SyncOperation operation, CancellationToken cancellationToken = default)
    {
        string path = SafetyStore.Asset(storeRoot, "operations", operation.Id, ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); SyncFiles.NoLinks(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await JsonSerializer.SerializeAsync(output, operation, Json, cancellationToken); await output.FlushAsync(cancellationToken); output.Flush(true); }
            cancellationToken.ThrowIfCancellationRequested(); SyncFiles.NoLinks(path);
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<SyncOperation> LoadAsync(string storeRoot, string operationId, CancellationToken cancellationToken = default)
    {
        string path = SafetyStore.Asset(storeRoot, "operations", operationId, ".json");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous);
        var operation = await JsonSerializer.DeserializeAsync<SyncOperation>(stream, Json, cancellationToken) ?? throw new IOException("空同步记录。");
        if (operation.Id != operationId || operation.Entries is null || operation.Entries.Any(e => e is null || string.IsNullOrWhiteSpace(e.Id) || e.Before is null || e.After is null || !Enum.IsDefined(e.State) || !Enum.IsDefined(e.Action)) || operation.Entries.Select(e => e.Id).Distinct().Count() != operation.Entries.Count)
            throw new IOException("无效的同步记录。");
        return operation;
    }
    public async Task<SyncHistoryListing> ListAsync(string storeRoot, CancellationToken cancellationToken = default)
    {
        string directory = Path.Combine(SafetyStore.Root(storeRoot), "operations"); SyncFiles.NoLinks(directory);
        if (!Directory.Exists(directory)) return new([], []);
        var operations = new List<SyncOperation>(); var errors = new List<string>();
        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { operations.Add(await LoadAsync(storeRoot, Path.GetFileNameWithoutExtension(path), cancellationToken)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            { errors.Add(Path.GetFileName(path) + ": " + ex.Message); }
        }
        return new(operations.OrderByDescending(o => o.CreatedUtc).ToArray(), errors);
    }
}
