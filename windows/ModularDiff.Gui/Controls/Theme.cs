using System.IO;
using System.Text.Json;
using System.Windows.Media;

namespace ModularDiff.Gui.Controls;

/// <summary>Declarative theme engine: loads theme-*.json files from the themes/ directory
/// (same pattern as grammar files). Each JSON file defines a named color palette; users add
/// custom themes by dropping a file — no recompilation. All controls reference Theme properties,
/// so a theme switch is a single Apply() call.</summary>
public static class Theme
{
    private static readonly Dictionary<string, Dictionary<string, string>> Themes = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, Brush> _colors = [];
    public static string CurrentName { get; private set; } = "Light";
    public static event Action? Changed;

    // ─── brush properties (all route through _colors loaded from JSON) ───
    public static Brush WindowBackground => Get("WindowBackground");
    public static Brush PanelBackground => Get("PanelBackground");
    public static Brush PanelHeader => Get("PanelHeader");
    public static Brush Border => Get("Border");
    public static Brush Ink => Get("Ink");
    public static Brush Muted => Get("Muted");
    public static Brush Accent => Get("Accent");
    public static Brush AccentText => Get("AccentText");
    public static Brush DiffModified => Get("DiffModified");
    public static Brush DiffDeleted => Get("DiffDeleted");
    public static Brush DiffInserted => Get("DiffInserted");
    public static Brush DiffGhost => Get("DiffGhost");
    public static Brush DiffSelected => Get("DiffSelected");
    public static Brush ChangedText => Get("ChangedText");
    public static Brush ChangedBackground => Get("ChangedBackground");
    public static Brush ModifiedBar => Get("ModifiedBar");
    public static Brush DeletedBar => Get("DeletedBar");
    public static Brush InsertedBar => Get("InsertedBar");
    public static Brush SyntaxKeyword => Get("SyntaxKeyword");
    public static Brush SyntaxString => Get("SyntaxString");
    public static Brush SyntaxComment => Get("SyntaxComment");
    public static Brush SyntaxNumber => Get("SyntaxNumber");
    public static Brush SyntaxType => Get("SyntaxType");
    public static Brush Gutter => Get("Gutter");
    public static Brush Caret => Get("Caret");

    public static IReadOnlyList<string> Available => [.. Themes.Keys];
    /// <summary>Per-file load errors (e.g. "theme-nord.json: line 5 — invalid color").</summary>
    private static readonly List<string> _loadDiagnostics = [];
    public static IReadOnlyList<string> LoadDiagnostics => _loadDiagnostics;

    static Theme()
    {
        // Scan for theme-*.json in the themes/ directory beside the exe.
        string themesDir = Path.Combine(AppContext.BaseDirectory, "themes");
        if (Directory.Exists(themesDir))
            foreach (string file in Directory.GetFiles(themesDir, "theme-*.json"))
                LoadTheme(file);
        // Always ensure at least a default light theme is available.
        if (!Themes.ContainsKey("Light"))
            Themes["Light"] = BuiltInLight();
        if (!Themes.ContainsKey("Dark"))
            Themes["Dark"] = BuiltInDark();
        Apply("Light");
    }

    private static void LoadTheme(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            string name = doc.RootElement.GetProperty("name").GetString() ?? "Custom";
            var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.GetProperty("colors").EnumerateObject())
                colors[prop.Name] = prop.Value.GetString() ?? "#000000";
            Themes[name] = colors;
        }
        catch (System.Text.Json.JsonException ex)
        {
            _loadDiagnostics.Add($"{Path.GetFileName(path)}: 第 {ex.LineNumber + 1} 行 — {ex.Message}");
        }
        catch (KeyNotFoundException ex)
        {
            _loadDiagnostics.Add($"{Path.GetFileName(path)}: {ex.Message}");
        }
        catch (FormatException ex)
        {
            _loadDiagnostics.Add($"{Path.GetFileName(path)}: 颜色值无效 — {ex.Message}");
        }
    }

    public static void Apply(string name)
    {
        if (!Themes.TryGetValue(name, out var colors)) return;
        CurrentName = name;
        _colors = colors.ToDictionary(kvp => kvp.Key, kvp => Parse(kvp.Value), StringComparer.OrdinalIgnoreCase);
        Changed?.Invoke();
    }

    /// <summary>Toggle between light and dark (the most common use case).</summary>
    public static void SetDark(bool dark) => Apply(dark ? "Dark" : "Light");

    private static Brush Get(string name) =>
        _colors.TryGetValue(name, out var brush) ? brush : Brushes.Black;

    private static Brush Parse(string hex) =>
        (Brush)new BrushConverter().ConvertFromString(hex)!;

    // ─── built-in fallbacks (only used if theme files are missing) ───
    private static Dictionary<string, string> BuiltInLight() => new()
    {
        ["WindowBackground"] = "#F1F3F7", ["PanelBackground"] = "#FFFFFF", ["PanelHeader"] = "#E8ECF2",
        ["Border"] = "#DDE3EC", ["Ink"] = "#253041", ["Muted"] = "#8793A4",
        ["Accent"] = "#3569AE", ["AccentText"] = "#FFFFFF",
        ["DiffModified"] = "#FFF6D6", ["DiffDeleted"] = "#FFF0F0", ["DiffInserted"] = "#ECF7EF",
        ["DiffGhost"] = "#F2F4F7", ["DiffSelected"] = "#D9E8FF",
        ["ChangedText"] = "#A52D22", ["ChangedBackground"] = "#FFD4BC",
        ["ModifiedBar"] = "#EAA05B", ["DeletedBar"] = "#D76565", ["InsertedBar"] = "#65A67F",
        ["SyntaxKeyword"] = "#2E60AA", ["SyntaxString"] = "#98632C", ["SyntaxComment"] = "#6A8473",
        ["SyntaxNumber"] = "#7A3E9D", ["SyntaxType"] = "#0B7285",
        ["Gutter"] = "#F7F8FA", ["Caret"] = "#254E86",
    };

    private static Dictionary<string, string> BuiltInDark() => new()
    {
        ["WindowBackground"] = "#1E1E2E", ["PanelBackground"] = "#2A2A3C", ["PanelHeader"] = "#333348",
        ["Border"] = "#3D3D52", ["Ink"] = "#CDD6F4", ["Muted"] = "#6C7086",
        ["Accent"] = "#89B4FA", ["AccentText"] = "#1E1E2E",
        ["DiffModified"] = "#4A4A38", ["DiffDeleted"] = "#453038", ["DiffInserted"] = "#2F4A3A",
        ["DiffGhost"] = "#333344", ["DiffSelected"] = "#3B4A6B",
        ["ChangedText"] = "#F38BA8", ["ChangedBackground"] = "#5B3A48",
        ["ModifiedBar"] = "#FAB387", ["DeletedBar"] = "#EBA0AC", ["InsertedBar"] = "#A6E3A1",
        ["SyntaxKeyword"] = "#89B4FA", ["SyntaxString"] = "#F9E2AF", ["SyntaxComment"] = "#6C7086",
        ["SyntaxNumber"] = "#F5C2E7", ["SyntaxType"] = "#94E2D5",
        ["Gutter"] = "#252535", ["Caret"] = "#89B4FA",
    };
}
