using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModularDiff.Contracts;

namespace ModularDiff.Syntax.Engine;

/// <summary>Generic declarative syntax engine: reads grammar-*.json files from the package
/// directory, compiles regex rules, and tokenizes lines per extension. Adding a language
/// means dropping a new grammar JSON file — no engine changes, no recompilation.
/// Malformed files are reported in LoadDiagnostics (file, line, column) instead of silently
/// skipped, so the host can surface an actionable warning to the user.</summary>
public sealed class GrammarEngine : ISyntaxGrammar
{
    private readonly Dictionary<string, List<(Regex Pattern, SyntaxTokenKind Kind)>> _compiled = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _languageNames = new(StringComparer.OrdinalIgnoreCase);

    public string Id => "syntax-engine";
    public string DisplayName => "Declarative syntax highlighting";
    public IReadOnlyList<string> Extensions => [.. _compiled.Keys];
    public string LanguageName => "Auto";
    public IReadOnlyList<GrammarRule> Rules { get; } = [];
    /// <summary>Per-file load errors (e.g. "grammar-python.json: Invalid escape at line 8 col 5").</summary>
    private readonly List<string> _loadDiagnostics = [];
    public IReadOnlyList<string> LoadDiagnostics => _loadDiagnostics;

    public GrammarEngine()
    {
        string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
        string grammarDir = Path.Combine(dir, "grammars");
        if (!Directory.Exists(grammarDir)) grammarDir = dir;
        if (!Directory.Exists(grammarDir)) return;
        foreach (string file in Directory.EnumerateFiles(grammarDir, "grammar-*.json"))
            try { Load(File.ReadAllText(file)); }
            catch (Exception ex) when (ex is JsonException or ArgumentException or KeyNotFoundException
                or InvalidOperationException or FormatException or IOException or UnauthorizedAccessException)
            {
                // 逐文件隔离:一个坏文法只进诊断,不炸掉整个引擎/插件包(此前类型错误会中断全部 10 种语言)。
                _loadDiagnostics.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
    }

    private void Load(string json)
    {
        using var doc = JsonDocument.Parse(json);
        string language = doc.RootElement.GetProperty("language").GetString() ?? "unknown";
        // 先在局部构建,全部成功才提交:半途失败不再留下"已注册但零规则"的扩展(那种状态会让
        // Tokenize 返回 [] —— 声称支持却不着色 —— 而不是回退到别的提供者)。
        var extensions = new List<string>();
        foreach (var ext in doc.RootElement.GetProperty("extensions").EnumerateArray())
        {
            if (ext.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(ext.GetString()))
                throw new InvalidOperationException("extensions[] 必须是非空字符串");
            extensions.Add(ext.GetString()!);
        }
        var rules = new List<(int Priority, string Pattern, SyntaxTokenKind Kind)>();
        foreach (var rule in doc.RootElement.GetProperty("rules").EnumerateArray())
        {
            string pattern = rule.GetProperty("pattern").GetString()
                ?? throw new InvalidOperationException("rules[].pattern 必须是字符串");
            string kindStr = rule.GetProperty("kind").GetString() ?? "Default";
            int priority = rule.TryGetProperty("priority", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 5;
            if (!Enum.TryParse<SyntaxTokenKind>(kindStr, true, out var kind))
                throw new KeyNotFoundException($"rules[].kind '{kindStr}' 不是有效的 SyntaxTokenKind");
            _ = new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromMilliseconds(50)); // fail fast on bad escapes
            rules.Add((priority, pattern, kind));
        }
        foreach (string extension in extensions)
        {
            _languageNames[extension] = language;
            var compiled = new List<(Regex, SyntaxTokenKind)>();
            foreach (var (priority, pattern, kind) in rules.OrderBy(r => r.Priority))
                compiled.Add((new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromMilliseconds(20)), kind));
            _compiled[extension] = compiled;
        }
    }

    public IReadOnlyList<SyntaxToken>? Tokenize(string line, string extension)
    {
        if (line.Length == 0 || line.Length > 8192) return [];
        if (!_compiled.TryGetValue(extension, out var rules)) return null;
        var claimed = new bool[line.Length];
        var tokens = new List<SyntaxToken>();
        foreach (var (pattern, kind) in rules)
            try
            {
                foreach (Match match in pattern.Matches(line))
                {
                    bool free = true;
                    for (int i = match.Index; i < match.Index + match.Length && i < line.Length; i++)
                        if (claimed[i]) { free = false; break; }
                    if (!free) continue;
                    for (int i = match.Index; i < match.Index + match.Length && i < line.Length; i++) claimed[i] = true;
                    tokens.Add(new(match.Index, match.Length, kind));
                }
            }
            catch (RegexMatchTimeoutException) { break; }
        return tokens;
    }

    public string LanguageForExtension(string extension) =>
        _languageNames.TryGetValue(extension, out var name) ? name : "Unknown";
}
