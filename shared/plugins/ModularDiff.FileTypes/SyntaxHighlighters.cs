using System.Text.RegularExpressions;
using ModularDiff.Contracts;

namespace ModularDiff.FileTypes;

// ─── shared helpers ───

/// <summary>Line-oriented syntax highlighter: applies keyword/string/comment/number regexes in
/// priority order (comments > strings > keywords > numbers). Tokens don't overlap because
/// earlier matches shadow later ones through a simple position-claim set.</summary>
internal abstract class RegexSyntaxHighlighter
{
    private readonly (Regex Pattern, SyntaxTokenKind Kind)[] _rules;
    protected RegexSyntaxHighlighter(params (Regex Pattern, SyntaxTokenKind Kind)[] rules) => _rules = rules;

    public IReadOnlyList<SyntaxToken> Tokenize(string line)
    {
        if (line.Length == 0 || line.Length > 8192) return [];
        var claimed = new bool[line.Length];
        var tokens = new List<SyntaxToken>();
        foreach (var (pattern, kind) in _rules)
            try
            {
                foreach (Match match in pattern.Matches(line))
                {
                    bool free = true;
                    for (int i = match.Index; i < match.Index + match.Length; i++)
                        if (claimed[i]) { free = false; break; }
                    if (!free) continue;
                    for (int i = match.Index; i < match.Index + match.Length; i++) claimed[i] = true;
                    tokens.Add(new(match.Index, match.Length, kind));
                }
            }
            catch (RegexMatchTimeoutException) { break; }
        return tokens;
    }
}

// ─── C-family (C#, Java, TS, JS, C, C++, Swift, Kotlin, Go) ───

internal sealed class CFamilySyntax : RegexSyntaxHighlighter
{
    private static readonly Regex Comment = new(@"(//.*$|/\*[\s\S]*?\*/)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));
    private static readonly Regex String = new(@"(""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|`(?:[^`\\]|\\.)*`)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));
    private static readonly Regex Keyword = new(@"\b(abstract|as|async|await|base|bool|break|byte|case|catch|char|checked|class|const|continue|decimal|default|delegate|do|double|else|enum|event|explicit|extern|false|finally|fixed|float|for|foreach|get|goto|if|implicit|in|init|int|interface|internal|is|lock|long|namespace|new|null|object|operator|out|override|params|partial|private|protected|public|readonly|record|ref|return|sbyte|sealed|set|short|sizeof|stackalloc|static|string|struct|switch|this|throw|true|try|typeof|uint|ulong|unchecked|unsafe|ushort|using|var|virtual|void|volatile|where|while|yield|package|func|defer|go|range|select|chan|map|fallthrough|import|export|from|type|let|const|function|extends|implements|super|this|readonly|interface|enum)\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));
    private static readonly Regex Number = new(@"\b(0x[0-9a-fA-F]+|\d+\.?\d*[fFdDmM]?)\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));

    public CFamilySyntax() : base(
        (Comment, SyntaxTokenKind.Comment),
        (String, SyntaxTokenKind.String),
        (Keyword, SyntaxTokenKind.Keyword),
        (Number, SyntaxTokenKind.Number)) { }
}

// ─── Python ───

internal sealed class PythonSyntax : RegexSyntaxHighlighter
{
    private static readonly Regex Comment = new(@"#.*$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));
    private static readonly Regex String = new(@"(?<!\\)(""[^""\n]*""|'[^'\n]*')", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));
    private static readonly Regex Keyword = new(@"\b(and|as|assert|async|await|break|class|continue|def|del|elif|else|except|False|finally|for|from|global|if|import|in|is|lambda|None|nonlocal|not|or|pass|raise|return|True|try|while|with|yield|match|case)\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(30));
    private static readonly Regex Number = new(@"\b(0x[0-9a-fA-F]+|\d+\.?\d*)\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));
    private static readonly Regex Decorator = new(@"@\w+", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));

    public PythonSyntax() : base(
        (Comment, SyntaxTokenKind.Comment),
        (String, SyntaxTokenKind.String),
        (Keyword, SyntaxTokenKind.Keyword),
        (Number, SyntaxTokenKind.Number),
        (Decorator, SyntaxTokenKind.Type)) { }
}

// ─── Rust ───

internal sealed class RustSyntax : RegexSyntaxHighlighter
{
    private static readonly Regex Comment = new(@"(//.*$|/\*[\s\S]*?\*/)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));
    private static readonly Regex String = new(@"(""(?:[^""\\]|\\.)*"")", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));
    private static readonly Regex Keyword = new(@"\b(as|async|await|break|const|continue|crate|dyn|else|enum|extern|false|fn|for|if|impl|in|let|loop|match|mod|move|mut|pub|ref|return|self|Self|static|struct|super|trait|true|type|unsafe|use|where|while)\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(30));
    private static readonly Regex Number = new(@"\b(0x[0-9a-fA-F_]+|\d[\d_]*\.?\d*)\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));
    private static readonly Regex Lifetime = new(@"('[a-z_]\w*)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));

    public RustSyntax() : base(
        (Comment, SyntaxTokenKind.Comment),
        (String, SyntaxTokenKind.String),
        (Keyword, SyntaxTokenKind.Keyword),
        (Number, SyntaxTokenKind.Number),
        (Lifetime, SyntaxTokenKind.Type)) { }
}

// ─── HTML / XML / XAML ───

internal sealed class MarkupSyntax : RegexSyntaxHighlighter
{
    private static readonly Regex Comment = new(@"(<!--[\s\S]*?-->)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));
    private static readonly Regex Tag = new(@"(<[/\w][^>]*>)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(30));
    private static readonly Regex AttrValue = new(@"(""[^""]*"")", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));

    public MarkupSyntax() : base(
        (Comment, SyntaxTokenKind.Comment),
        (Tag, SyntaxTokenKind.Keyword),
        (AttrValue, SyntaxTokenKind.String)) { }
}

// ─── CSS / SCSS / LESS ───

internal sealed class CssSyntax : RegexSyntaxHighlighter
{
    private static readonly Regex Comment = new(@"(/\*[\s\S]*?\*/)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));
    private static readonly Regex String = new(@"(""[^""]*""|'[^']*')", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));
    private static readonly Regex AtRule = new(@"(@[\w-]+)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));
    private static readonly Regex Property = new(@"([\w-]+)(?=\s*:)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(30));
    private static readonly Regex Number = new(@"(\d+\.?\d*(px|em|rem|%|vh|vw|s|ms|deg)?|# [0-9a-fA-F]{3,8})|#[0-9a-fA-F]{3,8}", RegexOptions.Compiled, TimeSpan.FromMilliseconds(30));

    public CssSyntax() : base(
        (Comment, SyntaxTokenKind.Comment),
        (String, SyntaxTokenKind.String),
        (AtRule, SyntaxTokenKind.Keyword),
        (Property, SyntaxTokenKind.Type),
        (Number, SyntaxTokenKind.Number)) { }
}

// ─── JSON ───

internal sealed class JsonSyntax : RegexSyntaxHighlighter
{
    private static readonly Regex String = new(@"(""(?:[^""\\]|\\.)*"")", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));
    private static readonly Regex Number = new(@"\b(-?\d+\.?\d*[eE]?[+-]?\d*)\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));
    private static readonly Regex Keyword = new(@"\b(true|false|null)\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));

    public JsonSyntax() : base(
        (String, SyntaxTokenKind.String),
        (Number, SyntaxTokenKind.Number),
        (Keyword, SyntaxTokenKind.Keyword)) { }
}

// ─── YAML ───

internal sealed class YamlSyntax : RegexSyntaxHighlighter
{
    private static readonly Regex Comment = new(@"(#.*$)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));
    private static readonly Regex String = new(@"(""[^""]*""|'[^']*')", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));
    private static readonly Regex Key = new(@"^(\s*[\w.-]+)(?=\s*:)", RegexOptions.Compiled | RegexOptions.Multiline, TimeSpan.FromMilliseconds(20));
    private static readonly Regex Number = new(@"\b(\d+\.?\d*)\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));
    private static readonly Regex Bool = new(@"\b(true|false|null|yes|no|on|off)\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));

    public YamlSyntax() : base(
        (Comment, SyntaxTokenKind.Comment),
        (String, SyntaxTokenKind.String),
        (Key, SyntaxTokenKind.Type),
        (Number, SyntaxTokenKind.Number),
        (Bool, SyntaxTokenKind.Keyword)) { }
}

// ─── Shell / PowerShell ───

internal sealed class ShellSyntax : RegexSyntaxHighlighter
{
    private static readonly Regex Comment = new(@"(#.*$)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));
    private static readonly Regex String = new(@"(""[^""]*""|'[^']*')", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));
    private static readonly Regex Keyword = new(@"\b(if|then|else|elif|fi|for|while|do|done|case|esac|function|return|local|export|source|echo|exit|in|switch|param|begin|process|end|try|catch|finally|throw)\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(30));
    private static readonly Regex Variable = new(@"(\$\{?\w+\}?)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));

    public ShellSyntax() : base(
        (Comment, SyntaxTokenKind.Comment),
        (String, SyntaxTokenKind.String),
        (Keyword, SyntaxTokenKind.Keyword),
        (Variable, SyntaxTokenKind.Number)) { }
}

// ─── Markdown ───

internal sealed class MarkdownSyntax : RegexSyntaxHighlighter
{
    private static readonly Regex Heading = new(@"^(#{1,6}\s+.*)", RegexOptions.Compiled | RegexOptions.Multiline, TimeSpan.FromMilliseconds(20));
    private static readonly Regex Bold = new(@"(\*\*[^*]+\*\*)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));
    private static readonly Regex Italic = new(@"(\*[^*]+\*)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));
    private static readonly Regex Code = new(@"(`[^`]+`)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));
    private static readonly Regex Link = new(@"(\[[^\]]+\]\([^)]+\))", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));

    public MarkdownSyntax() : base(
        (Heading, SyntaxTokenKind.Keyword),
        (Code, SyntaxTokenKind.String),
        (Link, SyntaxTokenKind.Type),
        (Bold, SyntaxTokenKind.Operator),
        (Italic, SyntaxTokenKind.Comment)) { }
}

// ─── SQL ───

internal sealed class SqlSyntax : RegexSyntaxHighlighter
{
    private static readonly Regex Comment = new(@"(--.*$|/\*[\s\S]*?\*/)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));
    private static readonly Regex String = new(@"('(?:[^']|'')*')", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));
    private static readonly Regex Keyword = new(@"\b(SELECT|FROM|WHERE|INSERT|INTO|VALUES|UPDATE|SET|DELETE|CREATE|TABLE|ALTER|DROP|INDEX|VIEW|JOIN|INNER|LEFT|RIGHT|FULL|OUTER|ON|AS|AND|OR|NOT|NULL|IS|IN|EXISTS|BETWEEN|LIKE|ORDER|BY|GROUP|HAVING|LIMIT|OFFSET|UNION|ALL|DISTINCT|CASE|WHEN|THEN|ELSE|END|PRIMARY|KEY|FOREIGN|REFERENCES|CONSTRAINT|DEFAULT|CHECK|UNIQUE|CASCADE|BEGIN|COMMIT|ROLLBACK|TRANSACTION|GRANT|REVOKE|WITH|RECURSIVE|OVER|PARTITION|ROW|RANK|DENSE_RANK|COUNT|SUM|AVG|MIN|MAX|COALESCE|CAST|CONVERT)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
    private static readonly Regex Number = new(@"\b\d+\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));

    public SqlSyntax() : base(
        (Comment, SyntaxTokenKind.Comment),
        (String, SyntaxTokenKind.String),
        (Keyword, SyntaxTokenKind.Keyword),
        (Number, SyntaxTokenKind.Number)) { }
}

// ─── Dispatcher ───

/// <summary>Routes a file path to the appropriate syntax highlighter by extension.</summary>
public static class SyntaxRouter
{
    private static readonly Dictionary<string, RegexSyntaxHighlighter> Highlighters = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = new CFamilySyntax(),
        [".java"] = new CFamilySyntax(),
        [".ts"] = new CFamilySyntax(),
        [".tsx"] = new CFamilySyntax(),
        [".js"] = new CFamilySyntax(),
        [".jsx"] = new CFamilySyntax(),
        [".c"] = new CFamilySyntax(),
        [".h"] = new CFamilySyntax(),
        [".cpp"] = new CFamilySyntax(),
        [".hpp"] = new CFamilySyntax(),
        [".swift"] = new CFamilySyntax(),
        [".kt"] = new CFamilySyntax(),
        [".go"] = new CFamilySyntax(),
        [".py"] = new PythonSyntax(),
        [".rs"] = new RustSyntax(),
        [".html"] = new MarkupSyntax(),
        [".htm"] = new MarkupSyntax(),
        [".xml"] = new MarkupSyntax(),
        [".xaml"] = new MarkupSyntax(),
        [".config"] = new MarkupSyntax(),
        [".csproj"] = new MarkupSyntax(),
        [".css"] = new CssSyntax(),
        [".scss"] = new CssSyntax(),
        [".less"] = new CssSyntax(),
        [".json"] = new JsonSyntax(),
        [".jsonc"] = new JsonSyntax(),
        [".yaml"] = new YamlSyntax(),
        [".yml"] = new YamlSyntax(),
        [".sh"] = new ShellSyntax(),
        [".bash"] = new ShellSyntax(),
        [".zsh"] = new ShellSyntax(),
        [".ps1"] = new ShellSyntax(),
        [".psm1"] = new ShellSyntax(),
        [".md"] = new MarkdownSyntax(),
        [".markdown"] = new MarkdownSyntax(),
        [".sql"] = new SqlSyntax(),
    };

    public static Func<string, IReadOnlyList<SyntaxToken>>? ForExtension(string extension) =>
        Highlighters.TryGetValue(extension, out var highlighter) ? highlighter.Tokenize : null;
}
