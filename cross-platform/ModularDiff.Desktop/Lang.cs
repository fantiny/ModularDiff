using System.IO;
using System.Text.Json;

namespace ModularDiff.Desktop;

/// <summary>声明式语言包引擎(与 WPF Lang 同约定):languages/lang-*.json 即一种语言,放入即生效、免重编译。
/// 取值回退链:当前语言 → 中文(zh-CN 基准) → 键名本身。</summary>
public static class Lang
{
    private static readonly Dictionary<string, Dictionary<string, string>> Packs = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);
    public static string CurrentName { get; private set; } = "中文";
    public static string CurrentCode { get; private set; } = "zh-CN";
    public static event Action? Changed;

    public static IReadOnlyList<string> Available => [.. Packs.Keys];

    /// <summary>语言包加载错误(文件名+行号+原因);空列表 = 全部正常。宿主启动时汇总提示,不再静默跳过。</summary>
    private static readonly List<string> _diagnostics = [];
    public static IReadOnlyList<string> Diagnostics => _diagnostics;

    /// <summary>取本地化字符串;当前语言缺失时回退中文包,再缺失返回键名。</summary>
    public static string Get(string key)
    {
        if (_strings.TryGetValue(key, out var value)) return value;
        if (Packs.TryGetValue("中文", out var fallback) && fallback.TryGetValue(key, out var chinese)) return chinese;
        return key;
    }

    static Lang()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "languages");
        if (Directory.Exists(dir))
            foreach (string file in Directory.GetFiles(dir, "lang-*.json"))
                Load(file);
        if (Packs.Count > 0 && Packs.TryGetValue("中文", out var chinese)) _strings = chinese;
    }

    private static void Load(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            string name = doc.RootElement.GetProperty("name").GetString() ?? "Unknown";
            var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.GetProperty("strings").EnumerateObject())
                strings[prop.Name] = prop.Value.GetString() ?? "";
            Packs[name] = strings;
        }
        catch (JsonException ex)
        {
            _diagnostics.Add($"{Path.GetFileName(path)}: 第 {ex.LineNumber + 1} 行第 {ex.BytePositionInLine} 列 — {ex.Message}");
        }
        catch (KeyNotFoundException ex)
        {
            _diagnostics.Add($"{Path.GetFileName(path)}: 缺少必需字段 — {ex.Message}");
        }
        catch (Exception ex)
        {
            _diagnostics.Add($"{Path.GetFileName(path)}: {ex.Message}");
        }
    }

    public static void Apply(string name)
    {
        if (!Packs.TryGetValue(name, out var strings)) return;
        CurrentName = name;
        _strings = strings;
        CurrentCode = strings.GetValueOrDefault("_code", "zh-CN");
        Changed?.Invoke();
    }
}
