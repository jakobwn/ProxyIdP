using System.Net.Http.Headers;

namespace ProxyIdP.ExamplePlugin;

/// <summary>Adds the plugin identity token issued by ProxyIdP as bearer token to outgoing requests.</summary>
public sealed class ProxyIdPTokenHandler(Func<CancellationToken, Task<string>> getIdentityToken) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await getIdentityToken(ct));
        return await base.SendAsync(request, ct);
    }
}
