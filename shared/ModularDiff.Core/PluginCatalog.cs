using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using ModularDiff.Contracts;

namespace ModularDiff.Core;

public sealed class PluginCatalog : IPluginResolver, IDisposable
{
    private readonly Dictionary<string, IPlugin> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PluginLoadContext> _contexts = [];
    public List<string> Diagnostics { get; } = [];

    public static PluginCatalog Load(string directory)
    {
        var catalog = new PluginCatalog();
        if (!Directory.Exists(directory))
        {
            catalog.Diagnostics.Add($"Plugin directory does not exist: {directory}");
            return catalog;
        }
        foreach (var manifest in Directory.EnumerateFiles(directory, "plugin.json", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
            catalog.LoadPackage(manifest);
        catalog.ValidateDependencies();
        return catalog;
    }

    private void ValidateDependencies()
    {
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in _plugins.Values) Visit(plugin);
        void Visit(IPlugin plugin)
        {
            if (visited.Contains(plugin.Id)) return;
            if (!visiting.Add(plugin.Id)) { Diagnostics.Add($"Plugin dependency cycle at '{plugin.Id}'."); return; }
            foreach (var id in plugin.Dependencies)
            {
                if (_plugins.TryGetValue(id, out var dependency)) Visit(dependency);
                else Diagnostics.Add($"Plugin '{plugin.Id}' requires missing plugin '{id}'.");
            }
            visiting.Remove(plugin.Id);
            visited.Add(plugin.Id);
        }
    }

    private void LoadPackage(string manifestPath)
    {
        PluginLoadContext? context = null;
        var instances = new List<IPlugin>();
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = json.RootElement;
            if (root.GetProperty("apiVersion").GetInt32() != 1)
                throw new InvalidOperationException("Unsupported plugin API version (host supports 1).");
            var name = root.GetProperty("assembly").GetString() ?? throw new InvalidOperationException("Missing assembly.");
            if (Path.GetFileName(name) != name || !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("assembly must be a DLL filename within the package.");
            var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestPath)!, name));
            context = new PluginLoadContext(path);
            var assembly = context.LoadFromAssemblyPath(path);
            foreach (var type in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true }
                         && typeof(IPlugin).IsAssignableFrom(t)))
            {
                var plugin = (IPlugin)Activator.CreateInstance(type)!;
                instances.Add(plugin);
                if (string.IsNullOrWhiteSpace(plugin.Id) || plugin.ApiVersion != 1)
                    throw new InvalidOperationException($"Invalid plugin identity/API: {type.Name}");
                if (_plugins.ContainsKey(plugin.Id) || instances.Count(p => p.Id.Equals(plugin.Id, StringComparison.OrdinalIgnoreCase)) > 1)
                    throw new InvalidOperationException($"Duplicate plugin ID: {plugin.Id}");
            }
            if (instances.Count == 0) throw new InvalidOperationException("No public IPlugin implementations found.");
            // Register the entire package atomically, so a broken package cannot be half-loaded.
            foreach (var plugin in instances) _plugins.Add(plugin.Id, plugin);
            _contexts.Add(context);
        }
        catch (Exception ex)
        {
            foreach (var plugin in instances.OfType<IDisposable>()) plugin.Dispose();
            context?.Unload();
            Diagnostics.Add($"{manifestPath}: {ex.GetBaseException().Message}");
        }
    }

    public T Get<T>(string id) where T : class, IPlugin =>
        _plugins.TryGetValue(id, out var plugin) && plugin is T typed ? typed
        : throw new InvalidOperationException($"Plugin '{id}' does not provide {typeof(T).Name}.");
    public IReadOnlyList<T> GetAll<T>() where T : class, IPlugin =>
        _plugins.Values.OfType<T>().OrderBy(p => p.Id, StringComparer.Ordinal).ToArray();

    public void Dispose()
    {
        foreach (var plugin in _plugins.Values.OfType<IDisposable>()) plugin.Dispose();
        _plugins.Clear();
        foreach (var context in _contexts) context.Unload();
        _contexts.Clear();
    }

    private sealed class PluginLoadContext(string mainAssemblyPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(IPlugin).Assembly.GetName().Name) return typeof(IPlugin).Assembly;
            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
        protected override nint LoadUnmanagedDll(string name)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(name);
            return path is null ? 0 : LoadUnmanagedDllFromPath(path);
        }
    }
}
