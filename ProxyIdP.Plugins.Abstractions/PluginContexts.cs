using System.Security.Claims;

namespace ProxyIdP.Plugins;

/// <summary>Common data for all plugin hook contexts.</summary>
public abstract class PluginContext
{
    protected PluginContext(string clientId) => ClientId = clientId;

    /// <summary>The downstream client the flow belongs to.</summary>
    public string ClientId { get; }
}

/// <summary>A context whose hook can stop the flow.</summary>
public abstract class RejectablePluginContext : PluginContext
{
    protected RejectablePluginContext(string clientId) : base(clientId) { }

    public bool IsRejected { get; private set; }
    public string? Error { get; private set; }
    public string? ErrorDescription { get; private set; }

    /// <summary>Stops the flow and reports an OAuth error to the client.</summary>
    public void Reject(string error = "access_denied", string? description = null)
    {
        IsRejected = true;
        Error = error;
        ErrorDescription = description;
    }
}

/// <summary>A downstream client's authorization request has arrived.</summary>
public sealed class AuthorizationRequestContext(string clientId, string redirectUri, IReadOnlyList<string> scopes, string? loginHint)
    : RejectablePluginContext(clientId)
{
    public string RedirectUri { get; } = redirectUri;
    public IReadOnlyList<string> Scopes { get; } = scopes;
    public string? LoginHint { get; set; } = loginHint;
}

/// <summary>The user is about to be redirected to the upstream IdP.</summary>
public sealed class UpstreamRedirectContext(string clientId, string idpId, string? email, string authorizeUrl) : PluginContext(clientId)
{
    public string IdpId { get; } = idpId;
    public string? Email { get; } = email;

    /// <summary>The upstream authorize URL. May be replaced.</summary>
    public string AuthorizeUrl { get; set; } = authorizeUrl;

    public void AddQueryParameter(string name, string value) =>
        AuthorizeUrl += (AuthorizeUrl.Contains('?') ? "&" : "?") + Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value);
}

/// <summary>The upstream IdP has authenticated the user.</summary>
public sealed class UpstreamCallbackContext(string clientId, string idpId, string subject, IList<Claim> claims) : RejectablePluginContext(clientId)
{
    public string IdpId { get; } = idpId;
    public string Subject { get; } = subject;

    /// <summary>Upstream claims that will be carried into the proxy tokens. Mutable.</summary>
    public IList<Claim> Claims { get; } = claims;
}

/// <summary>The proxy is about to issue tokens.</summary>
public sealed class TokenIssuingContext(string clientId, string subject, string scope, IList<Claim> claims) : PluginContext(clientId)
{
    public string Subject { get; } = subject;
    public string Scope { get; } = scope;

    /// <summary>Claims that will be placed in the issued ID and access tokens. Mutable.</summary>
    public IList<Claim> Claims { get; } = claims;
}

/// <summary>The proxy has issued tokens.</summary>
public sealed class TokenIssuedContext(string clientId, string subject, string scope, IReadOnlyList<Claim> claims) : PluginContext(clientId)
{
    public string Subject { get; } = subject;
    public string Scope { get; } = scope;
    public IReadOnlyList<Claim> Claims { get; } = claims;
}

/// <summary>A proxy flow failed.</summary>
public sealed class ErrorContext(string? clientId, string stage, Exception exception) : PluginContext(clientId ?? string.Empty)
{
    public string Stage { get; } = stage;
    public Exception Exception { get; } = exception;
}
