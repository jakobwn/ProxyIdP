using System.Net.Http.Headers;
using System.Text.Json;

namespace ProxyIdP.ExamplePlugin;

/// <summary>
/// Exchanges the ProxyIdP-issued plugin identity token for an Entra access token (client_credentials with a
/// federated client assertion) and adds it as bearer token to outgoing requests.
/// </summary>
public sealed class EntraTokenHandler(
    IHttpClientFactory httpFactory,
    string tenantId,
    string clientId,
    string scope,
    Func<CancellationToken, Task<string>> getIdentityToken) : DelegatingHandler
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expires;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(ct));
        return await base.SendAsync(request, ct);
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (IsValid()) return _token!;
        await _gate.WaitAsync(ct);
        try
        {
            if (!IsValid()) (_token, _expires) = await ExchangeAsync(ct);
            return _token!;
        }
        finally { _gate.Release(); }
    }

    private bool IsValid() => _token is not null && DateTimeOffset.UtcNow < _expires - TimeSpan.FromSeconds(60);

    private async Task<(string Token, DateTimeOffset Expires)> ExchangeAsync(CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["scope"] = scope,
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = await getIdentityToken(ct)
        };

        var http = httpFactory.CreateClient();
        using var response = await http.PostAsync($"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token", new FormUrlEncodedContent(form), ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var token = root.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("Entra response had no access_token.");
        var seconds = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 300;
        return (token, DateTimeOffset.UtcNow.AddSeconds(seconds));
    }
}
