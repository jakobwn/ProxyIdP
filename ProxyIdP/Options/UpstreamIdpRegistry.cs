namespace ProxyIdP.Options;

public sealed class UpstreamIdpRegistry
{
    private readonly List<UpstreamIdpOptions> _idps;

    public UpstreamIdpRegistry(IEnumerable<UpstreamIdpOptions> idps)
    {
        _idps = idps.ToList();
        var errors = _idps.SelectMany(i => i.Validate()).ToList();
        errors.AddRange(_idps.GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => $"UpstreamIdps: duplicate Id '{g.Key}'."));
        var defaults = _idps.Where(i => i.IsDefault).ToList();
        if (defaults.Count > 1)
            errors.Add($"UpstreamIdps: only one IdP may have IsDefault=true (found: {string.Join(", ", defaults.Select(d => d.Id))}).");
        errors.AddRange(defaults.Where(d => !d.Enabled).Select(d => $"UpstreamIdps '{d.Id}': the default IdP must be enabled."));
        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid UpstreamIdps configuration:" + Environment.NewLine + string.Join(Environment.NewLine, errors));

        foreach (var idp in _idps)
        {
            if (!idp.Scopes.Contains("openid", StringComparer.Ordinal)) idp.Scopes.Insert(0, "openid");
            idp.EmailDomains = (idp.EmailDomains ?? [])
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => d.Trim().TrimStart('@').ToLowerInvariant()).ToList();
        }
    }

    public IReadOnlyList<UpstreamIdpOptions> Enabled => _idps.Where(i => i.Enabled).ToList();

    public UpstreamIdpOptions? Default => Enabled.FirstOrDefault(i => i.IsDefault);

    public UpstreamIdpOptions? Get(string? id) =>
        Enabled.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));

    public UpstreamIdpOptions? ResolveByEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var at = email.LastIndexOf('@');
        if (at < 1 || at == email.Length - 1) return null;
        var domain = email[(at + 1)..].Trim();
        return Enabled.FirstOrDefault(i => i.EmailDomains.Any(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase)));
    }
}
