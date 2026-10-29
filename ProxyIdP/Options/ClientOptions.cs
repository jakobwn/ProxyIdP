using Microsoft.Extensions.Configuration;

namespace ProxyIdP.Options;

public class ClientOptions
{
    public static readonly string[] DefaultScopes = ["openid", "profile", "email"];

    public string ClientId { get; set; } = "";
    // Optional. Omit for public clients that rely on PKCE. Stored hashed.
    public string? Secret { get; set; }
    public List<string> RedirectUris { get; set; } = [];
    public List<string> PostLogoutRedirectUris { get; set; } = [];
    // Empty means the default scopes (openid, profile, email).
    public List<string> AllowedScopes { get; set; } = [];

    public IEnumerable<string> Validate()
    {
        var label = $"Clients '{(string.IsNullOrWhiteSpace(ClientId) ? "<no id>" : ClientId)}'";
        if (string.IsNullOrWhiteSpace(ClientId)) yield return $"{label}: client id is required.";
        if (RedirectUris.Count == 0 || RedirectUris.Any(string.IsNullOrWhiteSpace))
            yield return $"{label}: at least one non-empty RedirectUri is required.";
    }

    public static List<ClientOptions> FromConfiguration(IConfiguration config)
    {
        var nodes = config.GetSection("Clients").GetChildren().ToList();
        var errors = new List<string>();
        errors.AddRange(nodes.Where(n => int.TryParse(n.Key, out _)).Select(n =>
            $"Clients: must be an object keyed by client id, not an array (found numeric key '{n.Key}')."));
        errors.AddRange(nodes.GroupBy(n => n.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => $"Clients: duplicate client id '{g.Key}'."));

        var clients = new List<ClientOptions>();
        foreach (var node in nodes.Where(n => !int.TryParse(n.Key, out _)))
        {
            var client = node.Get<ClientOptions>() ?? new ClientOptions();
            client.ClientId = node.Key;
            errors.AddRange(client.Validate());
            clients.Add(client);
        }
        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid Clients configuration:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        return clients;
    }
}
