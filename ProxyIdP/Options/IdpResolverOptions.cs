namespace ProxyIdP.Options;

public class IdpResolverOptions
{
    public string? Endpoint { get; set; }
    public string Method { get; set; } = "POST";
    public string? ApiKey { get; set; }
    public string ApiKeyHeader { get; set; } = "X-Api-Key";
    public int TimeoutSeconds { get; set; } = 3;
    public string EmailParameter { get; set; } = "email";

    public bool Enabled => !string.IsNullOrWhiteSpace(Endpoint);
}
