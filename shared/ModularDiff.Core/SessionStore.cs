using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModularDiff.Core;

/// <summary>A remembered comparison: named sessions are user-saved; recent entries are recorded automatically.</summary>
public sealed record SavedSession(
    string Name, string Left, string Right, string? Algorithm,
    bool IgnoreCase, bool IgnoreWhitespace, bool IgnoreEol, bool Recursive, DateTime SavedAt,
    IReadOnlyList<string>? UnimportantPatterns = null);

/// <summary>Persisted session lists shared by the GUIs (WPF and Desktop) through one JSON file,
/// mirroring Beyond Compare's named sessions and recent-comparations lists.</summary>
public static class SessionStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() },
    };
    public const int RecentLimit = 10;
    public static string DefaultPath =>
        Path.Combine(AppPaths.DataDirectory, "Sessions.json");

    public sealed record SessionFile(IReadOnlyList<SavedSession> Named, IReadOnlyList<SavedSession> Recent);

    public static SessionFile Load(string? path = null)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path ?? DefaultPath));
            var root = document.RootElement;
            return new(Read(root, "Named"), Read(root, "Recent"));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new([], []);
        }
        catch
        {
            // 根节点非对象等结构异常同样按"无可加载会话"处理,不让畸形文件炸掉宿主启动。
            return new([], []);
        }
    }

    public static void Save(SessionFile sessions, string? path = null)
    {
        string file = path ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var payload = new { Named = sessions.Named, Recent = sessions.Recent };
        // 原子写:裸 WriteAllText 在崩溃/并发下会截断文件,全部命名会话随之永久丢失。
        AtomicFile.WriteAllText(file, JsonSerializer.Serialize(payload, Options));
    }

    /// <summary>Inserts or renames a saved session, keeping the list stable-ordered by name.</summary>
    public static SessionFile UpsertNamed(this SessionFile sessions, SavedSession session)
    {
        var named = sessions.Named.Where(s => !s.Name.Equals(session.Name, StringComparison.OrdinalIgnoreCase)).Append(session);
        return new([.. named.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)], sessions.Recent);
    }

    public static SessionFile RemoveNamed(this SessionFile sessions, string name) =>
        new([.. sessions.Named.Where(s => !s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))], sessions.Recent);

    /// <summary>Records an automatic recent comparison; identical path pairs move to the front, limited to <see cref="RecentLimit"/>.</summary>
    public static SessionFile RecordRecent(this SessionFile sessions, string left, string right, string? algorithm,
        bool ignoreCase, bool ignoreWhitespace, bool ignoreEol, bool recursive, IReadOnlyList<string>? unimportant = null)
    {
        bool SamePair(SavedSession s) =>
            string.Equals(s.Left, left, StringComparison.OrdinalIgnoreCase) && string.Equals(s.Right, right, StringComparison.OrdinalIgnoreCase);
        var entry = new SavedSession("", left, right, algorithm, ignoreCase, ignoreWhitespace, ignoreEol, recursive, DateTime.Now, unimportant);
        var recent = sessions.Recent.Where(s => !SamePair(s)).Prepend(entry).Take(RecentLimit);
        return new(sessions.Named, [.. recent]);
    }

    private static IReadOnlyList<SavedSession> Read(JsonElement root, string property)
    {
        // 属性缺失或不是数组(如字符串)都按空处理:类型错误不该炸掉整个 Load(2026-11 审查)。
        if (!root.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array) return [];
        var result = new List<SavedSession>();
        foreach (var item in list.EnumerateArray())
        {
            try
            {
                result.Add(new SavedSession(
                    item.GetProperty("Name").GetString() ?? "",
                    item.GetProperty("Left").GetString() ?? "",
                    item.GetProperty("Right").GetString() ?? "",
                    item.TryGetProperty("Algorithm", out var algorithm) ? algorithm.GetString() : null,
                    item.TryGetProperty("IgnoreCase", out var ignoreCase) && ignoreCase.GetBoolean(),
                    item.TryGetProperty("IgnoreWhitespace", out var ignoreWhitespace) && ignoreWhitespace.GetBoolean(),
                    item.TryGetProperty("IgnoreEol", out var ignoreEol) && ignoreEol.GetBoolean(),
                    !item.TryGetProperty("Recursive", out var recursive) || recursive.GetBoolean(),
                    item.TryGetProperty("SavedAt", out var savedAt) && DateTime.TryParse(savedAt.GetString(), out var stamp) ? stamp : DateTime.MinValue,
                    item.TryGetProperty("UnimportantPatterns", out var patterns) && patterns.ValueKind == JsonValueKind.Array
                        ? [.. patterns.EnumerateArray().Select(p => p.GetString() ?? "").Where(p => p.Length > 0)] : null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* 跳过畸形条目(缺键/类型错),其余照常加载 */ }
        }
        return result;
    }
}
