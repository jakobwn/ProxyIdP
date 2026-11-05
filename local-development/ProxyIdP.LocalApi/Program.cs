using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using ProxyIdP.LocalApi.Options;
using ProxyIdP.LocalApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<LocalApiOptions>(builder.Configuration.GetSection(LocalApiOptions.SectionName));
builder.Services.AddSingleton<UserDirectory>();
builder.Services.AddSingleton<IdpMapper>();

var azure = builder.Configuration.GetSection("AzureAd");
var allowedClients = azure.GetSection("AllowedClientIds").Get<string[]>() ?? [];

var auth = builder.Configuration.GetSection("ProxyIdP");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        // Tokens are issued by ProxyIdP itself; the role claim (plugin/resolver) is asserted by the controllers.
        o.Authority = auth["Issuer"];
        o.Audience = auth["Audience"];
        o.RequireHttpsMetadata = false;
        o.MapInboundClaims = false;
        o.TokenValidationParameters.RoleClaimType = "role";
        o.TokenValidationParameters.NameClaimType = "sub";
    });

// Example: validating Entra tokens obtained via a federated identity credential instead.
// builder.Services.AddAuthentication().AddJwtBearer(o =>
// {
//     o.Authority = $"https://login.microsoftonline.com/{azure["TenantId"]}/v2.0";
//     o.Audience = azure["Audience"];
//     o.Events = new JwtBearerEvents { OnTokenValidated = ctx => { /* check azp against allowedClients */ return Task.CompletedTask; } };
// });
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddControllers();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
