using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ProxyIdP.Plugins;

/// <summary>
/// Base class for ProxyIdP plugins. Override only the hooks you need.
/// Plugins need a public parameterless constructor.
/// </summary>
public abstract class ProxyIdPPlugin
{
    /// <summary>Unique plugin name. Also used as the environment variable prefix for plugin configuration.</summary>
    public abstract string Name { get; }

    /// <summary>Plugins run in ascending order.</summary>
    public virtual int Order => 0;

    /// <summary>The plugin's own service provider (built from <see cref="ConfigureServices"/>).</summary>
    protected IServiceProvider Services { get; private set; } = null!;

    /// <summary>Configuration read from the plugin's own folder.</summary>
    protected IConfiguration Configuration { get; private set; } = null!;

    protected ILogger Logger { get; private set; } = null!;

    /// <summary>The identity assigned to this plugin by ProxyIdP. Null if the host did not provide one.</summary>
    protected IPluginIdentity Identity { get; private set; } = null!;

    /// <summary>Called by the host after <see cref="ConfigureServices"/>. Not for plugin authors.</summary>
    public void Attach(IServiceProvider services, IConfiguration configuration, ILogger logger) =>
        Attach(services, configuration, logger, null);

    /// <summary>Called by the host after <see cref="ConfigureServices"/>. Not for plugin authors.</summary>
    public void Attach(IServiceProvider services, IConfiguration configuration, ILogger logger, IPluginIdentity? identity)
    {
        Services = services;
        Configuration = configuration;
        Logger = logger;
        Identity = identity!;
    }

    /// <summary>
    /// Returns the token that represents this plugin towards other systems. By default the ProxyIdP-issued identity token.
    /// Override to supply a different token; the subject of the host-issued identity remains host-controlled.
    /// </summary>
    protected virtual Task<string> GetIdentityTokenAsync(CancellationToken ct) => Identity.GetTokenAsync(ct);

    /// <summary>Public entry point for the host and tests to obtain the plugin's identity token.</summary>
    public Task<string> AcquireIdentityTokenAsync(CancellationToken ct) => GetIdentityTokenAsync(ct);

    /// <summary>Register plugin services. <paramref name="configuration"/> is scoped to the plugin folder.</summary>
    public virtual void ConfigureServices(IServiceCollection services, IConfiguration configuration) { }

    /// <summary>A client authorization request arrived. May reject or adjust it.</summary>
    public virtual Task OnAuthorizationRequestAsync(AuthorizationRequestContext context, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Just before the redirect to the upstream IdP.</summary>
    public virtual Task OnUpstreamRedirectAsync(UpstreamRedirectContext context, CancellationToken ct) => Task.CompletedTask;

    /// <summary>After the upstream IdP authenticated the user. May map claims or reject.</summary>
    public virtual Task OnUpstreamCallbackAsync(UpstreamCallbackContext context, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Just before tokens are issued. Add, change or remove claims here to enrich the token.</summary>
    public virtual Task OnTokenIssuingAsync(TokenIssuingContext context, CancellationToken ct) => Task.CompletedTask;

    /// <summary>After tokens were issued.</summary>
    public virtual Task OnTokenIssuedAsync(TokenIssuedContext context, CancellationToken ct) => Task.CompletedTask;

    /// <summary>A proxy flow failed.</summary>
    public virtual Task OnErrorAsync(ErrorContext context, CancellationToken ct) => Task.CompletedTask;
}
