using System.Security.Claims;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProxyIdP.Options;
using ProxyIdP.Plugins;

namespace ProxyIdP.Tests;

public class PluginHostTests : IDisposable
{
    private const string ExampleType = "ProxyIdP.ExamplePlugin.ExamplePlugin, ProxyIdP.ExamplePlugin";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "proxyidp-plugins-" + Guid.NewGuid().ToString("N"));

    public PluginHostTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class Env(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TestMonitor(PluginOptions initial) : IOptionsMonitor<PluginOptions>
    {
        private Action<PluginOptions, string?>? _listener;
        public PluginOptions CurrentValue { get; private set; } = initial;
        public PluginOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<PluginOptions, string?> listener)
        {
            _listener = listener;
            return null;
        }
        public void Set(PluginOptions value)
        {
            CurrentValue = value;
            _listener?.Invoke(value, null);
        }
    }

    private (PluginHost Host, TestMonitor Monitor) Create(string? path = null, params string[] types)
    {
        var monitor = new TestMonitor(new PluginOptions { Path = path ?? _root, Types = [.. types] });
        return (new PluginHost(monitor, new Env(_root), NullLoggerFactory.Instance), monitor);
    }

    private static string? FindExamplePluginOutput()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProxyIdP.sln"))) dir = dir.Parent;
        if (dir is null) return null;
        var bin = Path.Combine(dir.FullName, "local-development", "ProxyIdP.ExamplePlugin", "bin");
        return Directory.Exists(bin)
            ? Directory.EnumerateFiles(bin, "ProxyIdP.ExamplePlugin.dll", SearchOption.AllDirectories).Select(Path.GetDirectoryName).FirstOrDefault()
            : null;
    }

    private void CopyExamplePlugin()
    {
        var source = FindExamplePluginOutput() ?? throw new InvalidOperationException("Build ProxyIdP.ExamplePlugin first.");
        var target = Path.Combine(_root, "ExamplePlugin");
        Directory.CreateDirectory(target);
        foreach (var f in Directory.GetFiles(source, "ProxyIdP.ExamplePlugin.*").Concat(Directory.GetFiles(source, "appsettings*.json")))
            File.Copy(f, Path.Combine(target, Path.GetFileName(f)), true);
    }

    [Fact]
    public async Task MissingFolder_LoadsNoPlugins()
    {
        var (host, _) = Create(Path.Combine(_root, "does-not-exist"), ExampleType);
        using var _ = host;
        await host.StartAsync(default);
        Assert.Empty(host.Plugins);
    }

    [Fact]
    public async Task InvalidAssembly_IsSkipped_WithoutThrowing()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "broken.dll"), "not an assembly");
        var (host, _) = Create(null, "Some.Type, broken");
        using var _ = host;
        await host.StartAsync(default);
        Assert.Empty(host.Plugins);
    }

    [Fact]
    public async Task ListedType_IsLoaded_FromPluginFolder()
    {
        CopyExamplePlugin();
        var (host, _) = Create(null, ExampleType);
        using var _ = host;
        await host.StartAsync(default);

        var plugin = Assert.Single(host.Plugins);
        Assert.Equal("ExamplePlugin", plugin.Name);
        Assert.Equal(100, plugin.Order);
    }

    [Fact]
    public async Task UnlistedAssembly_IsIgnored()
    {
        CopyExamplePlugin();
        var (host, _) = Create(null);
        using var _ = host;
        await host.StartAsync(default);
        Assert.Empty(host.Plugins);
    }

    [Fact]
    public async Task UnknownType_IsSkipped()
    {
        CopyExamplePlugin();
        var (host, _) = Create(null, "Nope.Missing, ProxyIdP.ExamplePlugin", "Nope.Other, Missing.Assembly");
        using var _ = host;
        await host.StartAsync(default);
        Assert.Empty(host.Plugins);
    }

    [Fact]
    public async Task ConfigChange_AddsAndRemovesPlugin()
    {
        CopyExamplePlugin();
        var (host, monitor) = Create(null);
        using var _ = host;
        await host.StartAsync(default);
        Assert.Empty(host.Plugins);

        monitor.Set(new PluginOptions { Path = _root, Types = [ExampleType] });
        Assert.Single(host.Plugins);

        monitor.Set(new PluginOptions { Path = _root, Types = [] });
        Assert.Empty(host.Plugins);
    }

    [Fact]
    public async Task TokenIssuing_PluginAddsClaims_AndApiFailureDoesNotBreakFlow()
    {
        CopyExamplePlugin();
        var (host, _) = Create(null, ExampleType);
        using var _ = host;
        await host.StartAsync(default);

        var claims = new List<Claim> { new("sub", "u1"), new("email", "admin@example.com") };
        await host.TokenIssuingAsync(new TokenIssuingContext("client", "u1", "openid", claims), default);

        Assert.Contains(claims, c => c.Type == "example_plugin" && c.Value == "true");
    }

    [Fact]
    public async Task UpstreamRedirect_PluginCanAddQueryParameter()
    {
        CopyExamplePlugin();
        var (host, _) = Create(null, ExampleType);
        using var _ = host;
        await host.StartAsync(default);

        var ctx = new UpstreamRedirectContext("client", "idp", null, "https://idp.example/authorize?a=b");
        await host.UpstreamRedirectAsync(ctx, default);

        Assert.Contains("x-example=1", ctx.AuthorizeUrl);
    }
}
