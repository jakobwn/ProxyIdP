using System.Reflection;
using System.Runtime.Loader;

namespace ProxyIdP.Plugins;

/// <summary>
/// Collectible load context for one plugin. The abstractions and framework extension assemblies
/// are taken from the host so that plugin types match the host's types.
/// Assemblies are loaded from memory so the files on disk are never locked (allows rebuild while running).
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string mainAssemblyPath) : base(Path.GetFileNameWithoutExtension(mainAssemblyPath), isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    public Assembly LoadMain(string path) => LoadFromPath(path);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name is null) return null;
        if (name == "ProxyIdP.Plugins.Abstractions" || name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)) return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromPath(path);
    }

    private Assembly LoadFromPath(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        return LoadFromStream(stream);
    }
}
