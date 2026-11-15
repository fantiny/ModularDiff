using System.IO;
using System.Text.Json;

namespace ModularDiff.Gui.Controls;

/// <summary>Declarative language pack engine: loads lang-*.json files from the languages/
/// directory (same pattern as themes and grammar files). Each JSON file defines a named locale
/// with key-value string pairs; users add languages by dropping a file — no recompilation.</summary>
public static class Lang
{
    private static readonly Dictionary<string, Dictionary<string, string>> Packs = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);
    public static string CurrentName { get; private set; } = "中文";
    public static string CurrentCode { get; private set; } = "zh-CN";
    public static event Action? Changed;

    public static IReadOnlyList<string> Available => [.. Packs.Keys];
    /// <summary>Per-file load errors (e.g. "lang-fr.json: line 3 — missing 'strings'").</summary>
    private static readonly List<string> _loadDiagnostics = [];
    public static IReadOnlyList<string> LoadDiagnostics => _loadDiagnostics;

    /// <summary>Get a localized string; returns the key itself if no translation found.</summary>
    public static string Get(string key) =>
        _strings.TryGetValue(key, out var value) ? value : key;

    static Lang()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "languages");
        if (Directory.Exists(dir))
            foreach (string file in Directory.GetFiles(dir, "lang-*.json"))
                Load(file);
        if (Packs.Count > 0) Apply(Packs.Keys.First());
    }

    private static void Load(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            string name = doc.RootElement.GetProperty("name").GetString() ?? "Unknown";
            string code = doc.RootElement.GetProperty("code").GetString() ?? "xx";
            var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.GetProperty("strings").EnumerateObject())
                strings[prop.Name] = prop.Value.GetString() ?? "";
            Packs[name] = strings;
        }
        catch (System.Text.Json.JsonException ex)
        {
            _loadDiagnostics.Add($"{Path.GetFileName(path)}: 第 {ex.LineNumber + 1} 行 — {ex.Message}");
        }
        catch (KeyNotFoundException ex)
        {
            _loadDiagnostics.Add($"{Path.GetFileName(path)}: {ex.Message}");
        }
    }

    public static void Apply(string name)
    {
        if (!Packs.TryGetValue(name, out var strings)) return;
        CurrentName = name;
        _strings = strings;
        CurrentCode = strings.GetValueOrDefault("_code", "xx");
        Changed?.Invoke();
    }
}
