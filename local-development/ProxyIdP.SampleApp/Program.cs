using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var authority = builder.Configuration["ProxyIdP:Authority"] ?? "https://localhost:7103";
var clientId = builder.Configuration["ProxyIdP:ClientId"] ?? "dev-client";

builder.Services
    .AddAuthentication(o =>
    {
        o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        o.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie()
    .AddOpenIdConnect(o =>
    {
        o.Authority = authority;
        o.ClientId = clientId;
        o.ResponseType = "code";
        o.UsePkce = true;
        o.Scope.Clear();
        foreach (var s in new[] { "openid", "profile", "email" }) o.Scope.Add(s);
        o.MapInboundClaims = false;
        o.SaveTokens = true;
        o.GetClaimsFromUserInfoEndpoint = true;
        o.TokenValidationParameters.NameClaimType = "name";
        o.Events = new OpenIdConnectEvents
        {
            // The proxy's end_session endpoint needs client_id to validate post_logout_redirect_uri.
            OnRedirectToIdentityProviderForSignOut = ctx =>
            {
                ctx.ProtocolMessage.ClientId = ctx.Options.ClientId;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

string Enc(string? s) => WebUtility.HtmlEncode(s ?? "");

static string SignOutForm() => "<form method=\"post\" action=\"/signout\" style=\"display:inline\"><button type=\"submit\">Sign out</button></form>";

IResult Page(string title, string body) => Results.Content(
    "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>" + Enc(title) + "</title><style>" +
    "body{font-family:sans-serif;max-width:960px;margin:2em auto;padding:0 1em}" +
    "table{border-collapse:collapse;width:100%}td,th{border:1px solid #ccc;padding:.4em;text-align:left;vertical-align:top;word-break:break-all}" +
    "pre{background:#f4f4f4;padding:.8em;overflow:auto;white-space:pre-wrap;word-break:break-all}" +
    "button,a.btn{padding:.6em 1.2em;font-size:1em;cursor:pointer}.muted{color:#666}</style></head><body>" +
    body + "</body></html>", "text/html");

app.MapGet("/", (HttpContext ctx) =>
{
    var user = ctx.User;
    var sb = new StringBuilder("<h1>ProxyIdP sample app</h1>");
    sb.Append("<p>This app signs users in through the ProxyIdP using OpenID Connect (authorization code with PKCE).</p>");
    sb.Append($"<p class=\"muted\">Authority: <code>{Enc(authority)}</code><br>Client id: <code>{Enc(clientId)}</code></p>");
    if (user.Identity?.IsAuthenticated == true)
    {
        var name = user.FindFirstValue("name") ?? user.FindFirstValue("preferred_username") ?? user.FindFirstValue("email") ?? user.FindFirstValue("sub");
        sb.Append($"<p>Signed in as <strong>{Enc(name)}</strong>.</p><p><a class=\"btn\" href=\"/profile\">View profile</a> " + SignOutForm() + "</p>");
    }
    else
    {
        sb.Append("<p><a class=\"btn\" href=\"/signin\">Sign in</a></p>");
    }
    return Page("ProxyIdP sample app", sb.ToString());
});

app.MapGet("/signin", () => Results.Challenge(new AuthenticationProperties { RedirectUri = "/profile" },
    [OpenIdConnectDefaults.AuthenticationScheme]));

app.MapPost("/signout", () => Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
    [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]));

app.MapGet("/profile", async (HttpContext ctx) =>
{
    var sb = new StringBuilder("<h1>Profile</h1><p><a href=\"/\">Home</a> " + SignOutForm() + "</p><h2>Claims</h2>");
    sb.Append("<table><tr><th>Type</th><th>Value</th></tr>");
    foreach (var c in ctx.User.Claims.OrderBy(c => c.Type))
        sb.Append($"<tr><td>{Enc(c.Type)}</td><td>{Enc(c.Value)}</td></tr>");
    sb.Append("</table>");

    var expires = await ctx.GetTokenAsync("expires_at");
    if (expires is not null) sb.Append($"<p class=\"muted\">Access token expires at: {Enc(expires)}</p>");

    foreach (var (label, name) in new[] { ("ID token", "id_token"), ("Access token", "access_token") })
    {
        var token = await ctx.GetTokenAsync(name);
        sb.Append($"<h2>{label}</h2>");
        if (string.IsNullOrEmpty(token)) { sb.Append("<p>Not available.</p>"); continue; }
        sb.Append($"<h3>Raw</h3><pre>{Enc(token)}</pre>");
        var parts = token.Split('.');
        if (parts.Length >= 2)
        {
            sb.Append($"<h3>Header</h3><pre>{Enc(Pretty(parts[0]))}</pre><h3>Payload</h3><pre>{Enc(Pretty(parts[1]))}</pre>");
        }
    }

    sb.Append(SignOutForm());
    return Page("Profile", sb.ToString());
}).RequireAuthorization();

app.Run();

static string Pretty(string base64Url)
{
    try
    {
        using var doc = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(base64Url));
        return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }
    catch (Exception ex) when (ex is JsonException or FormatException)
    {
        return "(could not decode)";
    }
}

public partial class Program;
