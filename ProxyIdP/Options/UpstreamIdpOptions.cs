namespace ProxyIdP.Options;

public enum UpstreamIdpType
{
    EntraWorkforce,
    EntraExternalId,
    GenericOidc
}

public class UpstreamIdpOptions
{
    public string Id { get; set; } = "";
    public UpstreamIdpType Type { get; set; }
    public string DisplayName { get; set; } = "";
    public bool Enabled { get; set; } = true;
    // Display order; lower values first. Ties keep configuration order.
    public int SortOrder { get; set; }
    // Used when the email did not resolve to an IdP and no IdP was chosen explicitly.
    public bool IsDefault { get; set; }
    public string ClientId { get; set; } = "";
    // Optional. Omit for public clients that rely on PKCE.
    public string? ClientSecret { get; set; }
    public bool UsePkce { get; set; } = true;
    public List<string> Scopes { get; set; } = ["openid", "profile", "email"];
    public List<string> EmailDomains { get; set; } = [];

    // Entra workforce / Entra External ID
    public string? TenantId { get; set; }

    // Entra External ID
    public string? TenantSubdomain { get; set; }

    // Generic OIDC
    public string? Authority { get; set; }
    public string? MetadataAddress { get; set; }

    public bool IsEntra => Type is UpstreamIdpType.EntraWorkforce or UpstreamIdpType.EntraExternalId;

    public string GetMetadataAddress() => Type switch
    {
        UpstreamIdpType.EntraWorkforce => $"https://login.microsoftonline.com/{TenantId}/v2.0/.well-known/openid-configuration",
        UpstreamIdpType.EntraExternalId => $"https://{TenantSubdomain}.ciamlogin.com/{TenantId}/v2.0/.well-known/openid-configuration",
        _ => !string.IsNullOrWhiteSpace(MetadataAddress)
            ? MetadataAddress!
            : Authority!.TrimEnd('/') + "/.well-known/openid-configuration"
    };

    public IEnumerable<string> Validate()
    {
        var label = $"UpstreamIdps '{(string.IsNullOrWhiteSpace(Id) ? "<no id>" : Id)}'";
        if (string.IsNullOrWhiteSpace(Id) || !Id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
            yield return $"{label}: Id is required and may only contain letters, digits, '-' and '_'.";
        if (string.IsNullOrWhiteSpace(DisplayName)) yield return $"{label}: DisplayName is required.";
        if (string.IsNullOrWhiteSpace(ClientId)) yield return $"{label}: ClientId is required.";
        if (string.IsNullOrWhiteSpace(ClientSecret) && !UsePkce)
            yield return $"{label}: either ClientSecret or UsePkce must be configured.";

        switch (Type)
        {
            case UpstreamIdpType.EntraWorkforce:
                if (string.IsNullOrWhiteSpace(TenantId))
                    yield return $"{label}: TenantId is required for EntraWorkforce (GUID, domain, 'organizations' or 'common').";
                break;
            case UpstreamIdpType.EntraExternalId:
                if (!Guid.TryParse(TenantId, out _))
                    yield return $"{label}: TenantId must be a GUID for EntraExternalId.";
                if (string.IsNullOrWhiteSpace(TenantSubdomain))
                    yield return $"{label}: TenantSubdomain is required for EntraExternalId.";
                break;
            case UpstreamIdpType.GenericOidc:
                var address = !string.IsNullOrWhiteSpace(MetadataAddress) ? MetadataAddress : Authority;
                if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                    yield return $"{label}: Authority (or MetadataAddress) must be an absolute HTTPS URL for GenericOidc.";
                break;
        }
    }
}
