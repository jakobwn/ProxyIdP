namespace ProxyIdP.Options;

public class PluginOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Folder containing plugin assemblies. Relative paths are resolved against the content root.</summary>
    public string Path { get; set; } = "plugins";

    /// <summary>
    /// Assembly-qualified names of the plugin types to load, e.g. "MyCompany.MyPlugin.EnrichPlugin, MyCompany.MyPlugin".
    /// Only listed types are loaded. The list is re-read when the configuration changes.
    /// </summary>
    public List<string> Types { get; set; } = [];

    /// <summary>Audience of the identity tokens issued to plugins. Must match the audience of the Entra federated credential.</summary>
    public string IdentityAudience { get; set; } = "api://AzureADTokenExchange";

    /// <summary>Lifetime of issued plugin identity tokens.</summary>
    public int IdentityLifetimeSeconds { get; set; } = 300;

    /// <summary>
    /// Optional subject overrides, keyed by the exact entry in <see cref="Types"/>. Without an entry the subject is
    /// <c>plugin:{AssemblyName}/{TypeFullName}</c>.
    /// </summary>
    public Dictionary<string, string> Subjects { get; set; } = [];
}
