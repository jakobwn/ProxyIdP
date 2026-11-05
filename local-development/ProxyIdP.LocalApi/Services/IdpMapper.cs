using Microsoft.Extensions.Options;
using ProxyIdP.LocalApi.Options;

namespace ProxyIdP.LocalApi.Services;

public sealed class IdpMapper(IOptions<LocalApiOptions> options)
{
    private readonly LocalApiOptions _opt = options.Value;

    public string? Resolve(string? email)
    {
        var at = email?.LastIndexOf('@') ?? -1;
        if (at < 0) return null;
        var domain = email![(at + 1)..].ToLowerInvariant();
        return _opt.DomainMappings.TryGetValue(domain, out var idp) ? idp : _opt.DefaultIdpId;
    }
}
