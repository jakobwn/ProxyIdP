using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using ProxyIdP.Options;

namespace ProxyIdP.Plugins;

public interface IPluginHost
{
    IReadOnlyList<ProxyIdPPlugin> Plugins { get; }
    Task AuthorizationRequestAsync(AuthorizationRequestContext context, CancellationToken ct);
    Task UpstreamRedirectAsync(UpstreamRedirectContext context, CancellationToken ct);
    Task UpstreamCallbackAsync(UpstreamCallbackContext context, CancellationToken ct);
    Task TokenIssuingAsync(TokenIssuingContext context, CancellationToken ct);
    Task TokenIssuedAsync(TokenIssuedContext context, CancellationToken ct);
    Task ErrorAsync(ErrorContext context, CancellationToken ct);
}

/// <summary>
/// Loads the plugin types listed in <c>Plugins:Types</c> from the plugin folder and reloads them when the configuration changes.
/// </summary>
public sealed class PluginHost(
    IOptionsMonitor<PluginOptions> options,
    IHostEnvironment env,
    ILoggerFactory loggerFactory,
    IPluginIdentityIssuer? identityIssuer = null,
    TimeProvider? timeProvider = null) : IPluginHost, IHostedService, IDisposable
{
    private sealed class Snapshot(List<LoadedAssembly> loaded)
    {
        public List<LoadedAssembly> Loaded { get; } = loaded;
        public IReadOnlyList<ProxyIdPPlugin> Plugins { get; } = loaded.SelectMany(l => l.Plugins).OrderBy(p => p.Order).ToList();
    }

    private sealed record LoadedAssembly(PluginLoadContext Context, List<ProxyIdPPlugin> Plugins, ServiceProvider Services);

    private static readonly Snapshot Empty = new([]);

    private readonly ILogger _logger = loggerFactory.CreateLogger<PluginHost>();
    private readonly object _reloadLock = new();
    private volatile Snapshot _snapshot = Empty;
    private IDisposable? _subscription;

    public IReadOnlyList<ProxyIdPPlugin> Plugins => _snapshot.Plugins;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Reload(options.CurrentValue);
        _subscription = options.OnChange(Reload);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
        lock (_reloadLock)
        {
            Release(_snapshot, TimeSpan.Zero);
            _snapshot = Empty;
        }
    }

    private void Reload(PluginOptions opt)
    {
        lock (_reloadLock)
        {
            try
            {
                var next = new Snapshot(opt.Enabled ? LoadAll(opt) : []);
                var previous = _snapshot;
                _snapshot = next;
                Release(previous, TimeSpan.FromSeconds(30));
                _logger.LogInformation("Loaded {Count} plugin(s): {Names}", next.Plugins.Count, string.Join(", ", next.Plugins.Select(p => p.Name)));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plugin reload failed; keeping previously loaded plugins");
            }
        }
    }

    private static void Release(Snapshot snapshot, TimeSpan delay)
    {
        if (snapshot.Loaded.Count == 0) return;
        void Do()
        {
            foreach (var l in snapshot.Loaded)
            {
                try { l.Services.Dispose(); } catch { }
                try { l.Context.Unload(); } catch { }
            }
        }
        if (delay == TimeSpan.Zero) Do();
        else _ = Task.Delay(delay).ContinueWith(_ => Do());
    }

    private List<LoadedAssembly> LoadAll(PluginOptions opt)
    {
        var result = new List<LoadedAssembly>();
        var root = Path.GetFullPath(opt.Path, env.ContentRootPath);

        var entries = opt.Types
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(ParseTypeName)
            .Distinct()
            .ToList();
        if (entries.Count == 0) return result;

        foreach (var group in entries.GroupBy(e => e.Assembly, StringComparer.OrdinalIgnoreCase))
        {
            var assemblyPath = group.Key is null ? null : FindAssembly(root, group.Key);
            if (assemblyPath is null)
            {
                _logger.LogWarning("Plugin assembly {Assembly} was not found under {Path}; skipping {Types}", group.Key, root, string.Join(", ", group.Select(g => g.Type)));
                continue;
            }

            try
            {
                var loaded = LoadAssembly(assemblyPath, group.Select(g => g.Type).ToList(), opt, group.Key);
                if (loaded is not null) result.Add(loaded);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load plugin assembly {Assembly}; skipping", assemblyPath);
            }
        }
        return result;
    }

    internal static (string Type, string? Assembly) ParseTypeName(string typeName)
    {
        var parts = typeName.Split(',', 2, StringSplitOptions.TrimEntries);
        var assembly = parts.Length > 1 ? parts[1].Split(',')[0].Trim() : null;
        return (parts[0], string.IsNullOrEmpty(assembly) ? null : assembly);
    }

    private static string? FindAssembly(string root, string assemblyName)
    {
        if (!Directory.Exists(root)) return null;
        var direct = Path.Combine(root, assemblyName + ".dll");
        if (File.Exists(direct)) return direct;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var candidate = Path.Combine(dir, assemblyName + ".dll");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private LoadedAssembly? LoadAssembly(string assemblyPath, List<string> typeNames, PluginOptions opt, string? assemblyName)
    {
        var folder = Path.GetDirectoryName(assemblyPath)!;
        var context = new PluginLoadContext(assemblyPath);
        ServiceProvider? provider = null;
        try
        {
            var assembly = context.LoadMain(assemblyPath);
            var plugins = new List<ProxyIdPPlugin>();
            var configs = new Dictionary<ProxyIdPPlugin, IConfiguration>();
            var services = new ServiceCollection();
            services.AddSingleton(loggerFactory);
            services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

            foreach (var typeName in typeNames)
            {
                var type = assembly.GetType(typeName);
                if (type is null || !typeof(ProxyIdPPlugin).IsAssignableFrom(type) || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null)
                {
                    _logger.LogWarning("Type {Type} was not found in {Assembly} or is not a usable ProxyIdPPlugin; skipping", typeName, assemblyPath);
                    continue;
                }

                try
                {
                    var plugin = (ProxyIdPPlugin)Activator.CreateInstance(type)!;
                    var config = BuildConfiguration(folder, plugin.Name);
                    plugin.ConfigureServices(services, config);
                    plugins.Add(plugin);
                    configs[plugin] = config;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Plugin type {Type} failed to initialize; skipping", typeName);
                }
            }

            if (plugins.Count == 0)
            {
                context.Unload();
                return null;
            }

            provider = services.BuildServiceProvider();
            foreach (var plugin in plugins)
            {
                var type = plugin.GetType();
                var entry = opt.Types.FirstOrDefault(t => ParseTypeName(t) is var p && p.Type == type.FullName &&
                    string.Equals(p.Assembly, assemblyName, StringComparison.OrdinalIgnoreCase)) ?? $"{type.FullName}, {assemblyName}";
                var subject = PluginSubjectResolver.Resolve(entry, type.Assembly.GetName().Name ?? assemblyName ?? "", type.FullName ?? type.Name, opt.Subjects);
                IPluginIdentity? identity = identityIssuer is null ? null : new PluginIdentity(subject, identityIssuer, timeProvider ?? TimeProvider.System);
                plugin.Attach(provider, configs[plugin], loggerFactory.CreateLogger($"Plugin.{plugin.Name}"), identity);
            }
            return new LoadedAssembly(context, plugins, provider);
        }
        catch
        {
            provider?.Dispose();
            context.Unload();
            throw;
        }
    }

    private IConfiguration BuildConfiguration(string folder, string pluginName)
    {
        var files = new PhysicalFileProvider(folder);
        return new ConfigurationBuilder()
            .AddJsonFile(files, "appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile(files, $"appsettings.{env.EnvironmentName}.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(pluginName + "_")
            .Build();
    }

    public Task AuthorizationRequestAsync(AuthorizationRequestContext c, CancellationToken ct) =>
        RunAsync("authorization-request", p => p.OnAuthorizationRequestAsync(c, ct), () => c.IsRejected);

    public Task UpstreamRedirectAsync(UpstreamRedirectContext c, CancellationToken ct) =>
        RunAsync("upstream-redirect", p => p.OnUpstreamRedirectAsync(c, ct));

    public Task UpstreamCallbackAsync(UpstreamCallbackContext c, CancellationToken ct) =>
        RunAsync("upstream-callback", p => p.OnUpstreamCallbackAsync(c, ct), () => c.IsRejected);

    public Task TokenIssuingAsync(TokenIssuingContext c, CancellationToken ct) =>
        RunAsync("token-issuing", p => p.OnTokenIssuingAsync(c, ct));

    public Task TokenIssuedAsync(TokenIssuedContext c, CancellationToken ct) =>
        RunAsync("token-issued", p => p.OnTokenIssuedAsync(c, ct));

    public Task ErrorAsync(ErrorContext c, CancellationToken ct) =>
        RunAsync("error", p => p.OnErrorAsync(c, ct));

    private async Task RunAsync(string stage, Func<ProxyIdPPlugin, Task> hook, Func<bool>? stopWhen = null)
    {
        foreach (var plugin in _snapshot.Plugins)
        {
            try { await hook(plugin); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Plugin {Plugin} failed in {Stage}", plugin.Name, stage);
            }
            if (stopWhen?.Invoke() == true) return;
        }
    }
}
