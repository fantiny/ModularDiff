using System.IO;
using System.Text.Json;
using ModularDiff.Core;

namespace ModularDiff.Desktop;

/// <summary>声明式语言包引擎,与 WPF 的 Lang 同约定 —— 两个界面共用同一批 languages/lang-*.json。
/// <para>一个语言包有两张表:<c>strings</c> 把界面键映射到文本(宿主自身的界面文字);
/// <c>messages</c> 把 Core / 插件产生的文本(异常消息、备注、动作标签、显示名)映射到译文
/// (经 <see cref="Text"/>)—— Core 与插件因此保持与界面无关,又能跟随所选语言。</para>
/// <para>两份实现必须保持一致:任何一侧少一张表或少一条回退,用户就会在那个平台看到原始键、
/// 枚举名或未翻译的插件文本(同步预览曾因此在 macOS 上显示 "file_status_modified")。</para></summary>
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

    /// <summary>逐文件加载错误,以及语言包之间的键不一致(缺键意味着某个界面会显示键名)。</summary>
    private static readonly List<string> _loadDiagnostics = [];
    public static IReadOnlyList<string> LoadDiagnostics => _loadDiagnostics;

    /// <summary>取界面文本;当前语言缺失时回退英文,再缺失返回键名。空值视为缺失。</summary>
    public static string Get(string key)
    {
        if (_strings.TryGetValue(key, out var value) && value.Length > 0) return value;
        if (_english is not null && _english.TryGetValue(key, out var english) && english.Length > 0) return english;
        return key;
    }

    /// <summary>带 {0}、{1}… 占位符的界面文本;模板有问题时返回模板本身而不是抛异常。</summary>
    public static string Get(string key, params object[] args)
    {
        string template = Get(key);
        try { return args.Length == 0 ? template : string.Format(template, args); }
        catch (FormatException) { return template; }
    }

    /// <summary>翻译来自 Core 或插件的文本(消息、备注、动作标签、显示名)。中文包承载反向表
    /// (英文来源 → 中文)且不回退英文;其它语言缺条目时回退英文,再回退原文。</summary>
    public static string Text(string? source)
    {
        if (string.IsNullOrEmpty(source)) return source ?? "";
        string translated = _messages.Translate(source);
        if (!ReferenceEquals(translated, source) || IsChinese || ReferenceEquals(_messages, _englishMessages)) return translated;
        return _englishMessages.Translate(source);
    }

    /// <summary>插件经 Lang 键发出的文本(sync_*/file_status_*);其余来源文本走 <see cref="Text"/>。</summary>
    public static string PluginText(string? source) =>
        source is not null && (source.StartsWith("sync_", StringComparison.Ordinal) || source.StartsWith("file_status_", StringComparison.Ordinal))
            ? Get(source) : Text(source);

    private static bool IsChinese => CurrentCode.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    static Lang()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "languages");
        if (Directory.Exists(dir))
            foreach (string file in Directory.GetFiles(dir, "lang-*.json").Order(StringComparer.Ordinal))
                Load(file);
        ValidateKeyParity();
        if (Packs.Count > 0)
            Apply(Packs.Keys.FirstOrDefault(k => PackCodes.GetValueOrDefault(k) == "zh-CN") ?? Packs.Keys.First());
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
            Packs[name] = strings;
            PackCodes[name] = code;
            if (code.Equals("en-US", StringComparison.OrdinalIgnoreCase))
            {
                // 英文表在加载时就绪:其它语言缺条目时的回退不依赖"本次进程是否切换过一次英文"。
                _english = strings;
                _englishMessages = Catalogs[name];
            }
        }
        catch (JsonException ex)
        {
            _loadDiagnostics.Add($"{Path.GetFileName(path)}: 第 {ex.LineNumber + 1} 行第 {ex.BytePositionInLine} 列 — {ex.Message}");
        }
        catch (KeyNotFoundException ex)
        {
            _loadDiagnostics.Add($"{Path.GetFileName(path)}: 缺少必需字段 — {ex.Message}");
        }
        catch (Exception ex)
        {
            // 能解析但类型不对的语言包、或读不了的文件:报告出来,绝不让异常逃出静态构造函数炸掉启动。
            _loadDiagnostics.Add($"{Path.GetFileName(path)}: {ex.Message}");
        }
    }

    /// <summary>每个语言包必须提供同一套键(否则某种语言下会显示键名)。</summary>
    private static void ValidateKeyParity()
    {
        if (Packs.Count < 2) return;
        var reference = Packs.Values.OrderByDescending(p => p.Count).First();
        foreach (var (name, strings) in Packs)
        {
            foreach (string key in reference.Keys)
                if (!strings.ContainsKey(key)) _loadDiagnostics.Add($"lang '{name}' missing key: {key}");
            foreach (string key in strings.Keys)
                if (!reference.ContainsKey(key)) _loadDiagnostics.Add($"lang '{name}' extra key not in largest pack: {key}");
        }
    }

    /// <summary>切换本进程的语言(重新应用当前语言不通知;未知语言忽略)。</summary>
    public static void Apply(string name)
    {
        if (!Packs.TryGetValue(name, out var strings)) return;
        if (string.Equals(CurrentName, name, StringComparison.Ordinal) && ReferenceEquals(_strings, strings)) return;
        CurrentName = name;
        _strings = strings;
        CurrentCode = PackCodes.GetValueOrDefault(name) ?? strings.GetValueOrDefault("_code", "xx");
        _messages = Catalogs.GetValueOrDefault(name) ?? MessageCatalog.Empty;
        Changed?.Invoke();
    }
}
