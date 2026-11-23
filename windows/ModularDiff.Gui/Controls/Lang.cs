using System.IO;
using System.Text.Json;
using ModularDiff.Core;

namespace ModularDiff.Gui.Controls;

/// <summary>Declarative language pack engine: loads lang-*.json files from the languages/
/// directory (same pattern as themes and grammar files). Each JSON file defines a named locale
/// with key-value string pairs; users add languages by dropping a file — no recompilation.
/// <para>A pack has two tables. <c>strings</c> maps UI keys to text (host chrome). <c>messages</c>
/// maps strings that originate in Core or plugins — exception messages, notes, action labels,
/// display names — to their translation (<see cref="Text"/>); Core and plugins therefore stay
/// UI-neutral while still following the selected language.</para></summary>
public static class Lang
{
    private static readonly Dictionary<string, Dictionary<string, string>> Packs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, MessageCatalog> Catalogs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> PackCodes = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string>? _english;
    private static MessageCatalog _messages = MessageCatalog.Empty, _englishMessages = MessageCatalog.Empty;
    public static string CurrentName { get; private set; } = "中文";
    public static string CurrentCode { get; private set; } = "zh-CN";
    public static event Action? Changed;

    public static IReadOnlyList<string> Available => [.. Packs.Keys];
    /// <summary>Per-file load errors and cross-pack key mismatches.</summary>
    private static readonly List<string> _loadDiagnostics = [];
    public static IReadOnlyList<string> LoadDiagnostics => _loadDiagnostics;

    /// <summary>Remembered pack name, so the chosen language survives restarts.</summary>
    private static string PreferencePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModularDiff", "ui-language.txt");

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

    /// <summary>Translates a string that came from Core or a plugin (message, note, action label,
    /// display name) into the current language. The Chinese packs carry the reverse table
    /// (English → Chinese) and never fall back to English; other languages fall back to English
    /// when their own entry is missing, then to the original text.</summary>
    public static string Text(string? source)
    {
        if (string.IsNullOrEmpty(source)) return source ?? "";
        string translated = _messages.Translate(source);
        if (!ReferenceEquals(translated, source) || CurrentCode.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            || ReferenceEquals(_messages, _englishMessages)) return translated;
        return _englishMessages.Translate(source);
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
            string preferred = SavedLanguage() is { } saved && Packs.ContainsKey(saved) ? saved
                : Packs.Keys.FirstOrDefault(k => PackCodes.GetValueOrDefault(k) == "zh-CN") ?? Packs.Keys.First();
            Apply(preferred);
        }
    }

    private static string? SavedLanguage()
    {
        try { return File.Exists(PreferencePath) ? File.ReadAllText(PreferencePath).Trim() : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
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
            var messages = new Dictionary<string, string>(StringComparer.Ordinal);
            if (doc.RootElement.TryGetProperty("messages", out var table) && table.ValueKind == JsonValueKind.Object)
                foreach (var prop in table.EnumerateObject())
                    messages[prop.Name] = prop.Value.GetString() ?? "";
            Catalogs[name] = MessageCatalog.Create(messages);
            _messageKeys[name] = [.. messages.Keys];
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
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // A malformed-but-parseable pack (wrong value types) or an unreadable file is reported,
            // never allowed to escape the static constructor and take the application down.
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

    /// <summary>Switches the active pack for this process (tests and in-session switches).
    /// To make the choice stick across restarts use <see cref="Select"/>.</summary>
    public static void Apply(string name)
    {
        if (!Packs.TryGetValue(name, out var strings)) return;
        CurrentName = name;
        _strings = strings;
        CurrentCode = strings.GetValueOrDefault("_code", PackCodes.GetValueOrDefault(name, "xx"));
        _messages = Catalogs.GetValueOrDefault(name) ?? MessageCatalog.Empty;
        if (PackCodes.GetValueOrDefault(name) is { } code && code.Equals("en-US", StringComparison.OrdinalIgnoreCase))
            _englishMessages = _messages;
        Changed?.Invoke();
    }

    /// <summary>User-driven language change: applies the pack and remembers it for next launch.</summary>
    public static void Select(string name)
    {
        Apply(name);
        try { AtomicFile.WriteAllText(PreferencePath, name); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* remembering is best-effort */ }
    }

    /// <summary>Test helper: keys present in the active pack (excluding meta).</summary>
    public static IReadOnlyCollection<string> CurrentKeys =>
        _strings.Keys.Where(k => !k.StartsWith('_')).ToArray();

    /// <summary>Test helper: message sources carried by the given pack (empty when unknown).</summary>
    public static IReadOnlyCollection<string> MessageSources(string packName) =>
        _messageKeys.TryGetValue(packName, out var keys) ? keys : [];
    private static readonly Dictionary<string, IReadOnlyCollection<string>> _messageKeys = new(StringComparer.OrdinalIgnoreCase);
}
