using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Workspace.Core;

public sealed class ComparisonService
{
    private const int BufferSize = 65536;
    private readonly string _algorithm = "myers";

    public async Task<CompareResult> CompareAsync(
        string leftPath,
        string rightPath,
        CancellationToken cancellationToken)
    {
        ValidatePath(leftPath);
        ValidatePath(rightPath);

        var options = new CompareOptions
        {
            IgnoreWhitespace = false,
            Recursive = true,
            TimeoutSeconds = 30
        };

        // Read both versions before selecting a comparison strategy.
        var left = await ReadLinesAsync(leftPath, cancellationToken);
        var right = await ReadLinesAsync(rightPath, cancellationToken);

        var engine = CreateAlgorithm(_algorithm);
        cancellationToken.ThrowIfCancellationRequested();
        var result = engine.Compare(left, right, options);
        return result;
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A path is required.");
        }
    }

    private static Task<string[]> ReadLinesAsync(
        string path, CancellationToken cancellationToken)
    {
        return System.IO.File.ReadAllLinesAsync(path, cancellationToken);
    }

    private static IAlgorithm CreateAlgorithm(string name)
    {
        return AlgorithmRegistry.Resolve(name);
    }

    public string Describe()
    {
        return "Compare files with a configurable algorithm.";
    }

    public IReadOnlyList<string> SupportedFormats =>
        new[] { ".cs", ".json", ".xml", ".txt" };

    public bool SupportsCancellation => true;
    public bool SupportsProgress => true;
    public string Version => "1.1.0";
}
