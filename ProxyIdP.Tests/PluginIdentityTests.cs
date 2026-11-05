using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyIdP.Plugins;

namespace ProxyIdP.Tests;

public class PluginIdentityTests
{
    private sealed class StubIssuer : IPluginIdentityIssuer
    {
        public int Calls { get; private set; }
        public Task<(string Token, DateTimeOffset Expires)> IssueAsync(string subject, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(($"token-for-{subject}", DateTimeOffset.UtcNow.AddMinutes(5)));
        }
    }

    private sealed class DefaultPlugin : ProxyIdPPlugin
    {
        public override string Name => "Default";
    }

    private sealed class OverridingPlugin : ProxyIdPPlugin
    {
        public override string Name => "Overriding";
        protected override Task<string> GetIdentityTokenAsync(CancellationToken ct) => Task.FromResult("custom-token");
    }

    private static IPluginIdentity CreateIdentity(string subject, IPluginIdentityIssuer issuer) =>
        new PluginIdentity(subject, issuer, TimeProvider.System);

    private static void Attach(ProxyIdPPlugin plugin, IPluginIdentity identity) =>
        plugin.Attach(null!, new ConfigurationBuilder().Build(), NullLogger.Instance, identity);

    [Fact]
    public void Subject_DefaultsToAssemblyAndType()
    {
        var subject = PluginSubjectResolver.Resolve("My.Type, My.Asm", "My.Asm", "My.Type", new Dictionary<string, string>());
        Assert.Equal("plugin:My.Asm/My.Type", subject);
    }

    [Fact]
    public void Subject_UsesConfiguredOverride()
    {
        var overrides = new Dictionary<string, string> { ["My.Type, My.Asm"] = "my-plugin" };
        Assert.Equal("my-plugin", PluginSubjectResolver.Resolve("My.Type, My.Asm", "My.Asm", "My.Type", overrides));
    }

    [Fact]
    public async Task Identity_IssuesTokenForSubject_AndCaches()
    {
        var issuer = new StubIssuer();
        var identity = CreateIdentity("plugin:a/b", issuer);

        Assert.Equal("token-for-plugin:a/b", await identity.GetTokenAsync(default));
        await identity.GetTokenAsync(default);
        Assert.Equal(1, issuer.Calls);
    }

    [Fact]
    public async Task Plugin_DefaultsToHostIssuedToken()
    {
        var plugin = new DefaultPlugin();
        Attach(plugin, CreateIdentity("plugin:a/b", new StubIssuer()));
        Assert.Equal("token-for-plugin:a/b", await plugin.AcquireIdentityTokenAsync(default));
    }

    [Fact]
    public async Task Plugin_CanOverrideIdentityToken()
    {
        var plugin = new OverridingPlugin();
        Attach(plugin, CreateIdentity("plugin:a/b", new StubIssuer()));
        Assert.Equal("custom-token", await plugin.AcquireIdentityTokenAsync(default));
    }
}
