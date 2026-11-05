# ProxyIdP.Plugins.Abstractions

Base class and context types for writing ProxyIdP plugins. A plugin is a class library with one or more classes derived from `ProxyIdPPlugin`. ProxyIdP loads it at runtime from the plugin folder; the ProxyIdP project never references your plugin.

## 1. Create the project

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
	<TargetFramework>net10.0</TargetFramework>
	<EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
	<PackageReference Include="ProxyIdP.Plugins.Abstractions" Version="1.0.0" />
  </ItemGroup>
</Project>
```

Always reference the abstractions package (and `Microsoft.Extensions.*`) at the same major version as the host. These assemblies are shared with the host so types match; do not ship private copies of them.

## 2. Write a plugin

```csharp
using System.Security.Claims;
using ProxyIdP.Plugins;

namespace MyCompany.MyPlugin;

public class EnrichPlugin : ProxyIdPPlugin
{
	public override string Name => "EnrichPlugin";
	public override int Order => 100;

	public override Task OnTokenIssuingAsync(TokenIssuingContext context, CancellationToken ct)
	{
		context.Claims.Add(new Claim("tenant", "contoso"));
		return Task.CompletedTask;
	}
}
```

Requirements: a public, non-abstract class with a public parameterless constructor.

## 3. Hooks

| Hook | When | Can do |
|---|---|---|
| `OnAuthorizationRequestAsync` | Client authorization request arrives | Change `LoginHint`, `Reject(error, description)` |
| `OnUpstreamRedirectAsync` | Before redirecting to the upstream IdP | Change `AuthorizeUrl`, `AddQueryParameter` |
| `OnUpstreamCallbackAsync` | Upstream IdP authenticated the user | Edit `Claims`, `Reject(...)` |
| `OnTokenIssuingAsync` | Before tokens are issued | Add, change or remove `Claims` (end up in ID and access token) |
| `OnTokenIssuedAsync` | After tokens are issued | Read-only inspection, e.g. auditing |
| `OnErrorAsync` | A proxy flow failed | Logging, alerting |

Override only what you need. Plugins run in ascending `Order`. If a hook throws, the error is logged and the plugin is skipped for that call; the flow continues. Only `Reject` (in the authorization-request and upstream-callback hooks) stops a flow, and a rejection stops later plugins in that hook.

## 4. Services and configuration

Override `ConfigureServices(IServiceCollection, IConfiguration)` to register your own services (for example a typed `HttpClient`). Each plugin gets its own service provider; resolve from `Services` inside hooks. `Configuration` and `Logger` are available as protected properties after loading.

```csharp
public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
{
	services.AddHttpClient<MyApiClient>(c => c.BaseAddress = new Uri(configuration["ApiBaseUrl"]!));
}
```

## 5. Plugin configuration

Configuration lives next to the plugin DLL, not in ProxyIdP. Sources, later overriding earlier:

1. `appsettings.json` in the plugin folder
2. `appsettings.{Environment}.json` in the plugin folder
3. Environment variables prefixed with the plugin `Name` and an underscore, e.g. `EnrichPlugin_ApiBaseUrl`

Make sure the json files are copied to the output (`CopyToOutputDirectory`).

## 6. Deploy

Place the plugin build output in the plugin folder (default `plugins`, setting `Plugins:Path`), either directly or in one subfolder level, e.g. `plugins/MyPlugin/MyCompany.MyPlugin.dll`. Then list the type in configuration. Only listed types are loaded:

```json
"Plugins": {
  "Enabled": true,
  "Path": "plugins",
  "Types": [ "MyCompany.MyPlugin.EnrichPlugin, MyCompany.MyPlugin" ]
}
```

The format is `Namespace.Type, AssemblyName`. In Docker, mount a volume at `/app/plugins` and set `Plugins__Types__0=MyCompany.MyPlugin.EnrichPlugin, MyCompany.MyPlugin`.

## 7. Reloading

Changing `Plugins:Types` (or the plugin files) in a reloadable configuration source triggers a reload: new plugins are loaded and swapped in atomically, and old ones are unloaded after a short grace period. If a reload fails, the previously loaded plugins stay active. Unknown assemblies or types are logged and skipped.

## 8. Plugin identity

ProxyIdP gives each plugin an identity it cannot choose itself. The subject is `plugin:{AssemblyName}/{TypeFullName}` by default, or an operator-defined value in ProxyIdP config, keyed by the entry in `Plugins:Types`:

```json
"Plugins": {
  "Subjects": { "MyCompany.MyPlugin.EnrichPlugin, MyCompany.MyPlugin": "enrich-plugin" },
  "IdentityAudience": "api://AzureADTokenExchange",
  "IdentityLifetimeSeconds": 300
}
```

Inside a plugin, `Identity.Subject` is the subject and `Identity.GetTokenAsync(ct)` returns a short-lived JWT signed by ProxyIdP (`iss` = `Proxy:Issuer`, `sub` = subject, `aud` = `IdentityAudience`). Use it as a client assertion, for example to get an Entra token via a federated credential whose issuer is ProxyIdP and whose subject is the plugin subject. ProxyIdP's JWKS must be reachable by Entra.

To use a different token, override `GetIdentityTokenAsync(CancellationToken)`. Obtain the effective token with `AcquireIdentityTokenAsync`.

## 9. Tips

- Keep hooks fast; they run inside the request.
- Handle your own timeouts for outbound calls.
- Do not hold static state across reloads; each reload uses a fresh load context.
- Use `Logger` for diagnostics.

A complete reference plugin is in `local-development/ProxyIdP.ExamplePlugin` in the ProxyIdP repository.
