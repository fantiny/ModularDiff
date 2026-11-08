using ModularDiff.Contracts;

namespace ModularDiff.Core;

public sealed class ComparisonEngine(IPluginResolver plugins)
{
    public Task<ComparisonResult> CompareAsync(ComparisonRequest request, string? moduleId = null,
        IProgress<ComparisonProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        request = request with { LeftPath = Path.GetFullPath(request.LeftPath), RightPath = Path.GetFullPath(request.RightPath) };
        if (request.EffectiveOptions.MaxTextBytes <= 0) throw new ArgumentException("MaxTextBytes must be positive.");
        _ = plugins.Get<IDiffAlgorithm>(request.AlgorithmId);
        var candidates = plugins.GetAll<IComparisonModule>().Where(m => m.CanCompare(request.LeftPath, request.RightPath)).ToArray();
        var module = moduleId is not null ? plugins.Get<IComparisonModule>(moduleId)
            : candidates.Length == 1 ? candidates[0]
            : throw new InvalidOperationException(candidates.Length == 0
                ? "Choose two existing files or two existing directories."
                : "Multiple comparison modules support these paths; select --module explicitly.");
        if (!module.CanCompare(request.LeftPath, request.RightPath))
            throw new InvalidOperationException($"Module '{module.Id}' cannot compare these paths.");
        return module.CompareAsync(request, plugins, progress, cancellationToken);
    }
}
