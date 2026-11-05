namespace ProxyIdP.Options;

public enum ResolverAuthMode
{
    /// <summary>Legacy behavior: send ApiKey if one is configured.</summary>
    Auto,
    None,
    ApiKey,
    ClientSecret,
    FederatedCredential,
    AssertionFile
}

public class IdpResolverAuthenticationOptions
{
    public ResolverAuthMode Mode { get; set; } = ResolverAuthMode.Auto;

    public string? TokenEndpoint { get; set; }
    public string? ClientId { get; set; }
    public string? Scope { get; set; }

    /// <summary>ClientSecret mode.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>FederatedCredential mode: the assertion subject (sub).</summary>
    public string? Subject { get; set; }

    /// <summary>FederatedCredential mode: the assertion audience (aud).</summary>
    public string Audience { get; set; } = "api://AzureADTokenExchange";

    public int AssertionLifetimeSeconds { get; set; } = 300;

    /// <summary>AssertionFile mode: file containing a projected workload identity token.</summary>
    public string? AssertionFilePath { get; set; }

    public bool UsesBearerToken => Mode is ResolverAuthMode.ClientSecret or ResolverAuthMode.FederatedCredential or ResolverAuthMode.AssertionFile;
}

public class IdpResolverOptions
{
    public string? Endpoint { get; set; }
    public string Method { get; set; } = "POST";
    public string? ApiKey { get; set; }
    public string ApiKeyHeader { get; set; } = "X-Api-Key";
    public int TimeoutSeconds { get; set; } = 3;
    public string EmailParameter { get; set; } = "email";
    public IdpResolverAuthenticationOptions Authentication { get; set; } = new();

    public bool Enabled => !string.IsNullOrWhiteSpace(Endpoint);

    /// <summary>Returns an error message when the configuration is invalid, otherwise null.</summary>
    public string? Validate(bool isDevelopment)
    {
        if (!Enabled) return null;
        var a = Authentication;
        switch (a.Mode)
        {
            case ResolverAuthMode.ApiKey when string.IsNullOrEmpty(ApiKey):
                return "IdpResolver:ApiKey is required when Authentication:Mode is ApiKey.";
            case ResolverAuthMode.ClientSecret when string.IsNullOrEmpty(a.ClientSecret):
                return "IdpResolver:Authentication:ClientSecret is required for ClientSecret mode.";
            case ResolverAuthMode.FederatedCredential when string.IsNullOrEmpty(a.Subject):
                return "IdpResolver:Authentication:Subject is required for FederatedCredential mode.";
            case ResolverAuthMode.AssertionFile when string.IsNullOrEmpty(a.AssertionFilePath):
                return "IdpResolver:Authentication:AssertionFilePath is required for AssertionFile mode.";
        }
        if (!a.UsesBearerToken) return null;
        if (string.IsNullOrEmpty(a.TokenEndpoint) || !Uri.TryCreate(a.TokenEndpoint, UriKind.Absolute, out var uri))
            return "IdpResolver:Authentication:TokenEndpoint must be an absolute URL.";
        if (string.IsNullOrEmpty(a.ClientId)) return "IdpResolver:Authentication:ClientId is required.";
        if (!isDevelopment && uri.Scheme != Uri.UriSchemeHttps) return "IdpResolver:Authentication:TokenEndpoint must use HTTPS outside Development.";
        return null;
    }
}
