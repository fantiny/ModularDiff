using System.Text.Json;
using Avalonia.Media;
using ModularDiff.Core;

namespace ModularDiff.Desktop;

/// <summary>声明式主题引擎(与 WPF Theme 同约定):themes/theme-*.json 即一套配色,键名与 WPF 主题文件完全一致,
/// 两个 UI 复用同一批文件;放入新文件即成新主题,免重编译。内置色表仅作文件缺失时的兜底。
/// 自绘视图全部颜色走这里的属性,🌙 切换即整体换肤;明暗与语言选择持久化到 Settings.json。</summary>
public static class Palette
{
    private static readonly Dictionary<string, Dictionary<string, IBrush>> Themes = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, IBrush> _colors = [];
    private static readonly (bool Dark, string? Language) Settings = LoadSettings();
    private static readonly IBrush TextSelectionBrush = new SolidColorBrush(Avalonia.Media.Color.FromArgb(0x80, 0x33, 0x99, 0xFF));

    public static string CurrentName { get; private set; } = "Light";
    public static bool IsDark { get; private set; }
    public static string? SavedLanguage { get; private set; }
    public static IReadOnlyList<string> Available => [.. Themes.Keys];
    /// <summary>主题文件加载错误(文件名+行号+原因);空列表 = 全部正常。</summary>
    private static readonly List<string> _diagnostics = [];
    public static IReadOnlyList<string> Diagnostics => _diagnostics;

    static Palette()
    {
        IsDark = Settings.Dark;
        SavedLanguage = Settings.Language;
        string dir = Path.Combine(AppContext.BaseDirectory, "themes");
        if (Directory.Exists(dir))
            foreach (string file in Directory.GetFiles(dir, "theme-*.json"))
                LoadTheme(file);
        if (!Themes.ContainsKey("Light")) Themes["Light"] = Compile(BuiltInLight());
        if (!Themes.ContainsKey("Dark")) Themes["Dark"] = Compile(BuiltInDark());
        string preferred = Settings.Dark ? "Dark" : "Light";
        Apply(Themes.ContainsKey(preferred) ? preferred : "Light", persist: false);
    }

    // —— 视图基色(键名映射 WPF 主题文件;TextSelection 为固定半透明,不入主题文件)——
    public static IBrush Ink => Get("Ink");
    public static IBrush Number => Get("Muted");
    public static IBrush Ghost => Get("DiffGhost");
    public static IBrush Deleted => Get("DiffDeleted");
    public static IBrush Inserted => Get("DiffInserted");
    public static IBrush Modified => Get("DiffModified");
    public static IBrush Selected => Get("DiffSelected");
    public static IBrush Word => Get("ChangedBackground");
    public static IBrush Bookmark => Get("Accent");
    public static IBrush TextSelection => TextSelectionBrush;

    // —— 语法着色(SyntaxTokenKind)——
    public static IBrush Keyword => Get("SyntaxKeyword");
    public static IBrush String => Get("SyntaxString");
    public static IBrush Comment => Get("SyntaxComment");
    public static IBrush NumberBrush => Get("SyntaxNumber");
    public static IBrush Type => Get("SyntaxType");
    public static IBrush Operator => Get("Ink");

    public static void SetDark(bool dark) => Apply(dark ? "Dark" : "Light");
    public static void Apply(string name) => Apply(name, persist: true);
    private static void Apply(string name, bool persist)
    {
        if (!Themes.TryGetValue(name, out var colors)) return;
        CurrentName = name;
        _colors = colors;
        IsDark = string.Equals(name, "Dark", StringComparison.OrdinalIgnoreCase);
        if (persist) Persist();
        Changed?.Invoke();
    }
    public static void SaveLanguage(string language) { SavedLanguage = language; Persist(); }
    private static void Persist()
    {
        try
        {
            string directory = AppPaths.DataDirectory;
            Directory.CreateDirectory(directory);
            AtomicFile.WriteAllText(Path.Combine(directory, "Settings.json"), JsonSerializer.Serialize(new SettingsRecord(IsDark, SavedLanguage)));
        }
        catch { /* 持久化失败不影响本次切换 */ }
    }

    /// <summary>主题切换通知(视图等自绘控件借此触发重绘)。</summary>
    public static event Action? Changed;

    private static IBrush Get(string key) => _colors.TryGetValue(key, out var brush) ? brush : Parse("#253041");
    private static IBrush Parse(string hex) => new SolidColorBrush(Color.Parse(hex));   // 无效颜色抛 FormatException,由加载诊断捕获

    private static void LoadTheme(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            string name = doc.RootElement.GetProperty("name").GetString() ?? "Custom";
            // 文件色覆盖在同模式内置色表之上:自定义主题缺键也有合理兜底。
            var colors = string.Equals(name, "Dark", StringComparison.OrdinalIgnoreCase) ? BuiltInDark() : BuiltInLight();
            foreach (var prop in doc.RootElement.GetProperty("colors").EnumerateObject())
                colors[prop.Name] = prop.Value.GetString() ?? "#000000";
            Themes[name] = Compile(colors);
        }
        catch (JsonException ex)
        {
            _diagnostics.Add($"{Path.GetFileName(path)}: 第 {ex.LineNumber + 1} 行 — {ex.Message}");
        }
        catch (KeyNotFoundException ex)
        {
            _diagnostics.Add($"{Path.GetFileName(path)}: 缺少必需字段 — {ex.Message}");
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // 颜色值非法 / 根节点非对象 / 文件被锁等,一律降级为诊断而不是逃出静态构造函数炸掉启动。
            _diagnostics.Add($"{Path.GetFileName(path)}: {ex.Message}");
        }
    }
    private static Dictionary<string, IBrush> Compile(Dictionary<string, string> colors)
    {
        var brushes = new Dictionary<string, IBrush>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in colors) brushes[key] = Parse(value);
        return brushes;
    }
    private static (bool Dark, string? Language) LoadSettings()
    {
        try
        {
            string path = Path.Combine(AppPaths.DataDirectory, "Settings.json");
            var settings = File.Exists(path) ? JsonSerializer.Deserialize<SettingsRecord>(File.ReadAllText(path)) : null;
            return (settings?.Dark == true, settings?.Language);
        }
        catch { return (false, null); }
    }
    private sealed record SettingsRecord(bool Dark, string? Language = null);

    // —— 内置兜底(仅当主题文件缺失时使用;键名与 theme-*.json 一致)——
    private static Dictionary<string, string> BuiltInLight() => new()
    {
        ["Ink"] = "#253041", ["Muted"] = "#8793A4", ["DiffGhost"] = "#F2F4F7",
        ["DiffDeleted"] = "#FFF0F0", ["DiffInserted"] = "#ECF7EF", ["DiffModified"] = "#FFF0E9", ["DiffSelected"] = "#D9E8FF",
        ["ChangedBackground"] = "#FFD4BC", ["Accent"] = "#8AB4FF",
        ["SyntaxKeyword"] = "#0550AE", ["SyntaxString"] = "#A31515", ["SyntaxComment"] = "#6A9955",
        ["SyntaxNumber"] = "#098658", ["SyntaxType"] = "#952DA1",
    };
    private static Dictionary<string, string> BuiltInDark() => new()
    {
        ["Ink"] = "#C9D1D9", ["Muted"] = "#6E7681", ["DiffGhost"] = "#161B22",
        ["DiffDeleted"] = "#3D2020", ["DiffInserted"] = "#1B2B20", ["DiffModified"] = "#3B2E20", ["DiffSelected"] = "#2C4059",
        ["ChangedBackground"] = "#7A4B2E", ["Accent"] = "#58A6FF",
        ["SyntaxKeyword"] = "#FF7B72", ["SyntaxString"] = "#A5D6FF", ["SyntaxComment"] = "#7D8590",
        ["SyntaxNumber"] = "#79C0FF", ["SyntaxType"] = "#D2A8FF",
    };
}
