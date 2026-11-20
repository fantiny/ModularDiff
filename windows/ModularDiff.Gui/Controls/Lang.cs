using System.IO;
using System.Text.Json;

namespace ModularDiff.Gui.Controls;

/// <summary>Declarative language pack engine: loads lang-*.json files from the languages/
/// directory (same pattern as themes and grammar files). Each JSON file defines a named locale
/// with key-value string pairs; users add languages by dropping a file — no recompilation.</summary>
public static class Lang
{
    private static readonly Dictionary<string, Dictionary<string, string>> Packs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> PackCodes = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string>? _english;
    public static string CurrentName { get; private set; } = "中文";
    public static string CurrentCode { get; private set; } = "zh-CN";
    public static event Action? Changed;

    public static IReadOnlyList<string> Available => [.. Packs.Keys];
    /// <summary>Per-file load errors and cross-pack key mismatches.</summary>
    private static readonly List<string> _loadDiagnostics = [];
    public static IReadOnlyList<string> LoadDiagnostics => _loadDiagnostics;

    /// <summary>Get a localized string; falls back to en-US, then the key.</summary>
    public static string Get(string key)
    {
        if (_strings.TryGetValue(key, out var value) && value.Length > 0) return value;
        if (_english is not null && _english.TryGetValue(key, out var en) && en.Length > 0) return en;
        return key;
    }

    /// <summary>Localized format string with <c>{0}</c>-style placeholders.</summary>
    public static string Get(string key, params object[] args)
    {
        string template = Get(key);
        try { return args.Length == 0 ? template : string.Format(template, args); }
        catch (FormatException) { return template; }
    }

    static Lang()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "languages");
        if (Directory.Exists(dir))
            foreach (string file in Directory.GetFiles(dir, "lang-*.json"))
                Load(file);
        ValidateKeyParity();
        if (Packs.Count > 0)
        {
            string preferred = Packs.Keys.FirstOrDefault(k => PackCodes.GetValueOrDefault(k) == "zh-CN")
                ?? Packs.Keys.First();
            Apply(preferred);
        }
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
            strings["_code"] = code;
            Packs[name] = strings;
            PackCodes[name] = code;
            if (code.Equals("en-US", StringComparison.OrdinalIgnoreCase))
                _english = strings;
        }
        catch (JsonException ex)
        {
            _loadDiagnostics.Add($"{Path.GetFileName(path)}: line {ex.LineNumber + 1} — {ex.Message}");
        }
        catch (KeyNotFoundException ex)
        {
            _loadDiagnostics.Add($"{Path.GetFileName(path)}: {ex.Message}");
        }
    }

    /// <summary>Ensures every pack exposes the same key set (required for "all five filled").</summary>
    private static void ValidateKeyParity()
    {
        if (Packs.Count < 2) return;
        var reference = Packs.Values.OrderByDescending(p => p.Count).First();
        foreach (var (name, strings) in Packs)
        {
            foreach (string key in reference.Keys)
                if (!strings.ContainsKey(key))
                    _loadDiagnostics.Add($"lang '{name}' missing key: {key}");
            foreach (string key in strings.Keys)
                if (!reference.ContainsKey(key))
                    _loadDiagnostics.Add($"lang '{name}' extra key not in largest pack: {key}");
        }
    }

    public static void Apply(string name)
    {
        if (!Packs.TryGetValue(name, out var strings)) return;
        CurrentName = name;
        _strings = strings;
        CurrentCode = strings.GetValueOrDefault("_code", PackCodes.GetValueOrDefault(name, "xx"));
        Changed?.Invoke();
    }

    /// <summary>Test helper: keys present in the active pack (excluding meta).</summary>
    public static IReadOnlyCollection<string> CurrentKeys =>
        _strings.Keys.Where(k => !k.StartsWith('_')).ToArray();
}
