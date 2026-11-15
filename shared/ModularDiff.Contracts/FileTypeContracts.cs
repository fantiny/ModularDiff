namespace ModularDiff.Contracts;

/// <summary>编辑上下文：文件类型插件收到操作请求时的目标信息。</summary>
public sealed record EditContext(IEditBuffer Buffer, int Line, int Column);

/// <summary>文件类型专属的编辑操作（右键菜单项）。</summary>
public sealed record FileTypeAction(string Label, string? Shortcut, Func<EditContext, string> Execute)
{
    /// <summary>返回替换整个缓冲区的新文本；返回 null 表示无操作。</summary>
    public Func<EditContext, string> Transform { get; init; } = _ => "";
}

/// <summary>语法着色令牌：文件类型插件提供的着色规则。</summary>
public sealed record SyntaxToken(int Start, int Length, SyntaxTokenKind Kind);
public enum SyntaxTokenKind { Keyword, String, Comment, Number, Type, Operator }

/// <summary>声明式语法规则：一条正则模式 → 一种令牌类型，按优先级依次应用（低优先级先匹配，
/// 已被占用的字符位置不再参与后续匹配——同 RegexSyntaxHighlighter 的 claiming 策略）。</summary>
public sealed record GrammarRule(string Pattern, SyntaxTokenKind Kind, int Priority);

/// <summary>语法高亮插件：声明式规则集，按扩展名路由。规则可从 JSON 语法文件加载，
/// 新语言无需修改宿主或引擎——添加语法文件即可（类似 VS Code 的 TextMate grammar 模式）。</summary>
public interface ISyntaxGrammar : IPlugin
{
    /// <summary>此语法覆盖的扩展名（含点，如 ".py"、".go"）。</summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>语言名称。</summary>
    string LanguageName { get; }

    /// <summary>声明式规则集（编译前的原始定义）。</summary>
    IReadOnlyList<GrammarRule> Rules { get; }

    /// <summary>加载声明式语法文件时的错误（文件名 + 行号 + 原因）；空列表 = 全部正常。
    /// 宿主在启动时汇总显示，用户可直接修正文件。</summary>
    IReadOnlyList<string> LoadDiagnostics { get; }

    /// <summary>对一行文本返回着色令牌；null 表示该扩展名不支持。</summary>
    IReadOnlyList<SyntaxToken>? Tokenize(string line, string extension);
}

/// <summary>文件类型感知插件：按扩展名注册，提供语言专属的编辑操作、语法着色与结构化比较。
/// 宿主在右键菜单、语法高亮和比较路由中自动发现并使用这些能力。</summary>
public interface IFileTypeProvider : IPlugin
{
    /// <summary>此文件类型覆盖的扩展名（含点，如 ".json"、".cs"）。</summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>语言 / 格式的可读名称（如 "JSON"、"C#"）。</summary>
    string LanguageName { get; }

    /// <summary>右键菜单中的编辑操作（按文件类型定制，如 JSON 的"格式化并排序键"、C# 的"注释/取消注释"）。</summary>
    IReadOnlyList<(string Label, string? Shortcut, Func<EditContext, string> Transform)> EditActions { get; }

    /// <summary>语法着色：对一行文本返回着色令牌；null 表示使用默认着色。</summary>
    IReadOnlyList<SyntaxToken>? HighlightLine(string line) => null;
}
