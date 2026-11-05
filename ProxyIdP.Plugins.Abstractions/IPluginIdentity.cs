namespace ProxyIdP.Plugins;

/// <summary>The identity ProxyIdP assigns to a plugin. The host decides the subject; plugins cannot change it.</summary>
public interface IPluginIdentity
{
    /// <summary>Subject (<c>sub</c>) of the issued identity token.</summary>
    string Subject { get; }

    /// <summary>Returns a short-lived JWT issued by ProxyIdP for this plugin. Cached until close to expiry.</summary>
    Task<string> GetTokenAsync(CancellationToken ct);
}
