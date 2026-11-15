using System.Text.Json;
using Avalonia.Media;

namespace ModularDiff.Desktop;

/// <summary>集中调色板(明/暗双套,灵感 Catppuccin Mocha):自绘视图全部颜色走这里,
/// 🌙 切换即整体换肤;选择持久化到 ~/Library/Application Support/ModularDiff/Settings.json。</summary>
public static class Palette
{
    public static bool IsDark { get; private set; } = Load();

    // —— 视图基色 ——
    public static IBrush Ink => Get(!IsDark ? "#253041" : "#C9D1D9");
    public static IBrush Number => Get(!IsDark ? "#8793A4" : "#6E7681");
    public static IBrush Ghost => Get(!IsDark ? "#F2F4F7" : "#161B22");
    public static IBrush Deleted => Get(!IsDark ? "#FFF0F0" : "#3D2020");
    public static IBrush Inserted => Get(!IsDark ? "#ECF7EF" : "#1B2B20");
    public static IBrush Modified => Get(!IsDark ? "#FFF0E9" : "#3B2E20");
    public static IBrush Selected => Get(!IsDark ? "#D9E8FF" : "#2C4059");
    public static IBrush Word => Get(!IsDark ? "#FFD4BC" : "#7A4B2E");
    public static IBrush Bookmark => Get(!IsDark ? "#8AB4FF" : "#58A6FF");
    public static IBrush TextSelection => new SolidColorBrush(Avalonia.Media.Color.FromArgb(0x80, 0x33, 0x99, 0xFF));

    // —— 语法着色(SyntaxTokenKind)——
    public static IBrush Keyword => Get(!IsDark ? "#0550AE" : "#FF7B72");
    public static IBrush String => Get(!IsDark ? "#A31515" : "#A5D6FF");
    public static IBrush Comment => Get(!IsDark ? "#6A9955" : "#7D8590");
    public static IBrush NumberBrush => Get(!IsDark ? "#098658" : "#79C0FF");
    public static IBrush Type => Get(!IsDark ? "#952DA1" : "#D2A8FF");
    public static IBrush Operator => Get(!IsDark ? "#253041" : "#C9D1D9");

    public static void SetDark(bool dark)
    {
        if (IsDark == dark) return;
        IsDark = dark;
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModularDiff");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "Settings.json"), JsonSerializer.Serialize(new Settings(dark)));
        }
        catch { /* 持久化失败不影响本次切换 */ }
        Changed?.Invoke();
    }

    /// <summary>主题切换通知(视图等自绘控件借此触发重绘)。</summary>
    public static event Action? Changed;
    private static readonly Dictionary<string, IBrush> Cache = [];
    private static IBrush Get(string color) => Cache.TryGetValue(color, out var brush) ? brush : Cache[color] = new SolidColorBrush(Color.Parse(color));
    private static bool Load()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModularDiff", "Settings.json");
            return File.Exists(path) && JsonSerializer.Deserialize<Settings>(File.ReadAllText(path))?.Dark == true;
        }
        catch { return false; }
    }
    private sealed record Settings(bool Dark);
}
