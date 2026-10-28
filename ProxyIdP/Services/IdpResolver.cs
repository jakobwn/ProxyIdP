using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using ProxyIdP.Options;

namespace ProxyIdP.Services;

public interface IIdpResolver
{
    Task<UpstreamIdpOptions?> ResolveAsync(string? email, CancellationToken ct);
}

public sealed class IdpResolver(
    UpstreamIdpRegistry registry,
    IHttpClientFactory httpFactory,
    IOptions<IdpResolverOptions> options,
    ILogger<IdpResolver> logger) : IIdpResolver
{
    public const string HttpClientName = "idp-resolver";
    private readonly IdpResolverOptions _opt = options.Value;

    public async Task<UpstreamIdpOptions?> ResolveAsync(string? email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        email = email.Trim();

        var byDomain = registry.ResolveByEmail(email);
        if (byDomain is not null) return byDomain;
        if (!_opt.Enabled) return null;

        var domain = email.Contains('@') ? email[(email.LastIndexOf('@') + 1)..] : "(invalid)";
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _opt.TimeoutSeconds)));

            var isGet = string.Equals(_opt.Method, "GET", StringComparison.OrdinalIgnoreCase);
            using var request = isGet
                ? new HttpRequestMessage(HttpMethod.Get, QueryHelpers.AddQueryString(_opt.Endpoint!, _opt.EmailParameter, email))
                : new HttpRequestMessage(HttpMethod.Post, _opt.Endpoint!)
                {
                    Content = JsonContent.Create(new Dictionary<string, string> { [_opt.EmailParameter] = email })
                };
            if (!string.IsNullOrEmpty(_opt.ApiKey)) request.Headers.TryAddWithoutValidation(_opt.ApiKeyHeader, _opt.ApiKey);
            request.Headers.Accept.ParseAdd("application/json");

            var http = httpFactory.CreateClient(HttpClientName);
            using var response = await http.SendAsync(request, cts.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("IdP resolver API returned {Status} for domain {Domain}", (int)response.StatusCode, domain);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cts.Token);
            if (string.IsNullOrWhiteSpace(body)) return null;
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(p.Name, "idpId", StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Value.ValueKind != JsonValueKind.String) return null;
                var idp = registry.Get(p.Value.GetString());
                if (idp is null) logger.LogWarning("IdP resolver API returned an unknown or disabled idpId for domain {Domain}", domain);
                return idp;
            }
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("IdP resolver API call failed for domain {Domain}: {Error}", domain, ex.GetType().Name);
            return null;
        }
    }
}
