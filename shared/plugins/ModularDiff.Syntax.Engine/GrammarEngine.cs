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
        var diagnostics = _loadDiagnostics;
        string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
        string grammarDir = Path.Combine(dir, "grammars");
        if (!Directory.Exists(grammarDir)) grammarDir = dir;
        foreach (string file in Directory.GetFiles(grammarDir, "grammar-*.json"))
            try { Load(File.ReadAllText(file)); }
            catch (JsonException ex)
            {
                diagnostics.Add($"{Path.GetFileName(file)}: 第 {ex.LineNumber + 1} 行第 {ex.BytePositionInLine} 列 — {ex.Message}");
            }
            catch (ArgumentException ex) // bad regex pattern
            {
                diagnostics.Add($"{Path.GetFileName(file)}: 正则无效 — {ex.Message}");
            }
            catch (KeyNotFoundException ex)
            {
                diagnostics.Add($"{Path.GetFileName(file)}: 缺少必需字段 — {ex.Message}");
            }
    }

    private void Load(string json)
    {
        using var doc = JsonDocument.Parse(json);
        string language = doc.RootElement.GetProperty("language").GetString() ?? "unknown";
        foreach (var ext in doc.RootElement.GetProperty("extensions").EnumerateArray())
        {
            string extension = ext.GetString()!;
            _languageNames[extension] = language;
            _compiled[extension] = [];
        }
        var rules = new List<(int Priority, string Pattern, SyntaxTokenKind Kind)>();
        foreach (var rule in doc.RootElement.GetProperty("rules").EnumerateArray())
        {
            string pattern = rule.GetProperty("pattern").GetString()!;
            string kindStr = rule.GetProperty("kind").GetString() ?? "Default";
            int priority = rule.TryGetProperty("priority", out var p) ? p.GetInt32() : 5;
            if (Enum.TryParse<SyntaxTokenKind>(kindStr, true, out var kind))
                _ = new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromMilliseconds(50)); // fail fast on bad escapes
            else
                throw new KeyNotFoundException($"rules[].kind '{kindStr}' 不是有效的 SyntaxTokenKind");
            rules.Add((priority, pattern, kind));
        }
        foreach (string extension in _compiled.Keys.Where(e => _languageNames[e] == language).ToList())
        {
            var compiled = _compiled[extension];
            foreach (var (priority, pattern, kind) in rules.OrderBy(r => r.Priority))
                compiled.Add((new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromMilliseconds(20)), kind));
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
