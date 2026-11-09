using System.Text;
using ModularDiff.Contracts;
using ModularDiff.Core;

Console.OutputEncoding = Encoding.UTF8;
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
    {
        Console.WriteLine("""
            ModularDiff — composable file and directory comparison
              ModularDiff.Cli plugins [--plugins PATH]
              ModularDiff.Cli compare LEFT RIGHT [options]

            Options:
              --algorithm myers|patience    Algorithm plugin ID (default myers)
              --module ID                  Comparison plugin ID (default auto)
              --format text|json           Formatter plugin ID (default text)
              --output PATH                Write report to a file instead of stdout
              --plugins PATH               Plugin root (default ../plugins beside host)
              --ignore-case                Ignore invariant character case
              --ignore-whitespace          Remove all whitespace within each line
              --ignore-eol                 Ignore CRLF/LF/CR and final-newline differences
              --unimportant REGEX          Mark regex-matched text as unimportant (repeatable, e.g. '\$Id[^$]*\$')
              --no-recursive               Compare only files at the directory root
              --max-text-mb N               Larger files use exact streaming byte comparison

            Exit codes: 0 equal, 1 different, 2 error/incomplete, 130 cancelled.
            Directory reports contain summaries; compare an individual file for line edits.
            """);
        return 0;
    }
    var command = args[0];
    if (command is not ("plugins" or "compare")) throw new ArgumentException($"Unknown command: {command}");
    int start = command == "compare" ? 3 : 1;
    if (args.Length < start) throw new ArgumentException("compare requires LEFT and RIGHT paths.");
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    var flags = new HashSet<string>(StringComparer.Ordinal);
    var knownValues = new HashSet<string> { "--algorithm", "--module", "--format", "--output", "--plugins", "--max-text-mb" };
    var knownFlags = new HashSet<string>(StringComparer.Ordinal) { "--ignore-case", "--ignore-whitespace", "--ignore-eol", "--no-recursive" };
    var unimportant = new List<string>();
    for (int i = start; i < args.Length; i++)
    {
        var option = args[i];
        if (knownFlags.Contains(option)) { if (!flags.Add(option)) throw new ArgumentException($"Repeated option: {option}"); }
        else if (knownValues.Contains(option))
        {
            if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Missing value for {option}");
            if (!values.TryAdd(option, args[i])) throw new ArgumentException($"Repeated option: {option}");
        }
        else if (option == "--unimportant") { if (++i >= args.Length) throw new ArgumentException("Missing value for --unimportant"); unimportant.Add(args[i]); }
        else throw new ArgumentException($"Unknown option: {option}");
    }
    string Value(string key, string fallback) => values.GetValueOrDefault(key, fallback);
    string pluginPath = Value("--plugins", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "plugins")));
    using var catalog = PluginCatalog.Load(pluginPath);
    foreach (var diagnostic in catalog.Diagnostics) Console.Error.WriteLine($"Plugin error: {diagnostic}");
    if (catalog.Diagnostics.Count != 0) return 2;
    if (command == "plugins")
    {
        foreach (var plugin in catalog.GetAll<IPlugin>())
            Console.WriteLine($"{plugin.Id,-15} {plugin.DisplayName} [{string.Join(", ", plugin.GetType().GetInterfaces().Where(t => t != typeof(IPlugin) && typeof(IPlugin).IsAssignableFrom(t)).Select(t => t.Name))}]");
        return 0;
    }
    long maxBytes = 8 * 1024 * 1024;
    if (values.TryGetValue("--max-text-mb", out var mb))
    {
        if (!int.TryParse(mb, out int limit) || limit <= 0 || limit > 1024) throw new ArgumentException("--max-text-mb must be 1..1024.");
        maxBytes = (long)limit * 1024 * 1024;
    }
    var request = new ComparisonRequest(args[1], args[2], Value("--algorithm", "myers"),
        new(flags.Contains("--ignore-case"), flags.Contains("--ignore-whitespace"), flags.Contains("--ignore-eol"), !flags.Contains("--no-recursive"), maxBytes,
            unimportant.Count > 0 ? unimportant : null));
    var formatter = catalog.Get<IResultFormatter>(Value("--format", "text"));
    var result = await new ComparisonEngine(catalog).CompareAsync(request, values.GetValueOrDefault("--module"), cancellationToken: cancellation.Token);
    if (values.TryGetValue("--output", out var output))
    {
        string fullOutput = Path.GetFullPath(output);
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (string input in new[] { Path.GetFullPath(args[1]), Path.GetFullPath(args[2]) })
            if (fullOutput.Equals(input, pathComparison) || (Directory.Exists(input) && fullOutput.StartsWith(Path.TrimEndingDirectorySeparator(input) + Path.DirectorySeparatorChar, pathComparison)))
                throw new ArgumentException("Report output must be outside the compared inputs.");
        // CreateNew deliberately prevents accidental overwriting of an existing file.
        await using var stream = new FileStream(fullOutput, FileMode.CreateNew, FileAccess.Write);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await formatter.WriteAsync(result, writer, cancellation.Token);
    }
    else await formatter.WriteAsync(result, Console.Out, cancellation.Token);
    return result.HasErrors ? 2 : result.HasDifferences ? 1 : 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 130; }
catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); return 2; }
