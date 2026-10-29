using Microsoft.Extensions.Configuration;
using ProxyIdP.Options;

namespace ProxyIdP.Tests;

public class UpstreamIdpTests
{
    private static UpstreamIdpOptions Idp(string id, UpstreamIdpType type, Action<UpstreamIdpOptions>? configure = null)
    {
        var o = new UpstreamIdpOptions { Id = id, Type = type, DisplayName = id, ClientId = "c" };
        configure?.Invoke(o);
        return o;
    }

    [Fact]
    public void Workforce_RequiresTenantId() =>
        Assert.Throws<InvalidOperationException>(() => new UpstreamIdpRegistry([Idp("a", UpstreamIdpType.EntraWorkforce)]));

    [Fact]
    public void ExternalId_RequiresGuidTenantAndSubdomain() =>
        Assert.Throws<InvalidOperationException>(() => new UpstreamIdpRegistry([Idp("a", UpstreamIdpType.EntraExternalId, o => o.TenantId = "not-a-guid")]));

    [Fact]
    public void GenericOidc_RequiresHttpsAuthority()
    {
        Assert.Throws<InvalidOperationException>(() => new UpstreamIdpRegistry([Idp("a", UpstreamIdpType.GenericOidc, o => o.Authority = "http://idp")]));
        Assert.Throws<InvalidOperationException>(() => new UpstreamIdpRegistry([Idp("a", UpstreamIdpType.GenericOidc)]));
    }

    [Fact]
    public void DuplicateIds_Rejected() =>
        Assert.Throws<InvalidOperationException>(() => new UpstreamIdpRegistry([
            Idp("a", UpstreamIdpType.GenericOidc, o => o.Authority = "https://x"),
            Idp("A", UpstreamIdpType.GenericOidc, o => o.Authority = "https://y")]));

    [Fact]
    public void MetadataAddresses_AreBuiltPerType()
    {
        var g = Guid.NewGuid().ToString();
        Assert.Equal($"https://login.microsoftonline.com/{g}/v2.0/.well-known/openid-configuration",
            Idp("w", UpstreamIdpType.EntraWorkforce, o => o.TenantId = g).GetMetadataAddress());
        Assert.Equal($"https://acme.ciamlogin.com/{g}/v2.0/.well-known/openid-configuration",
            Idp("e", UpstreamIdpType.EntraExternalId, o => { o.TenantId = g; o.TenantSubdomain = "acme"; }).GetMetadataAddress());
        Assert.Equal("https://idp.example/.well-known/openid-configuration",
            Idp("o", UpstreamIdpType.GenericOidc, o => o.Authority = "https://idp.example/").GetMetadataAddress());
    }

    [Fact]
    public void ResolveByEmail_MatchesDomain_AndIgnoresDisabled()
    {
        var reg = new UpstreamIdpRegistry([
            Idp("a", UpstreamIdpType.GenericOidc, o => { o.Authority = "https://x"; o.EmailDomains = ["contoso.com"]; }),
            Idp("b", UpstreamIdpType.GenericOidc, o => { o.Authority = "https://y"; o.EmailDomains = ["off.com"]; o.Enabled = false; })]);
        Assert.Equal("a", reg.ResolveByEmail("Bob@CONTOSO.com")?.Id);
        Assert.Null(reg.ResolveByEmail("bob@off.com"));
        Assert.Null(reg.ResolveByEmail("bob@unknown.com"));
        Assert.Null(reg.ResolveByEmail("not-an-email"));
        Assert.Null(reg.ResolveByEmail(null));
        Assert.Equal("b", reg.Get("a") is null ? null : "b");
        Assert.Null(reg.Get("b"));
    }

    private static IConfiguration Config(params Dictionary<string, string?>[] layers)
    {
        var b = new ConfigurationBuilder();
        foreach (var l in layers) b.AddInMemoryCollection(l);
        return b.Build();
    }

    [Fact]
    public void FromConfiguration_UsesKeyAsId_AndLaterLayerOverridesSingleValue()
    {
        var reg = UpstreamIdpRegistry.FromConfiguration(Config(
            new Dictionary<string, string?> { ["UpstreamIdps:acme:Type"] = "GenericOidc", ["UpstreamIdps:acme:DisplayName"] = "Acme",
                    ["UpstreamIdps:acme:ClientId"] = "old", ["UpstreamIdps:acme:Authority"] = "https://acme.example" },
            new Dictionary<string, string?> { ["UpstreamIdps:acme:ClientId"] = "new" }));
        var idp = Assert.Single(reg.Enabled);
        Assert.Equal("acme", idp.Id);
        Assert.Equal("new", idp.ClientId);
    }

    [Fact]
    public void Enabled_IsOrderedBySortOrder_ThenKeepsInputOrder()
    {
        var reg = new UpstreamIdpRegistry([
            Idp("c", UpstreamIdpType.GenericOidc, o => { o.Authority = "https://c"; o.SortOrder = 20; }),
            Idp("z", UpstreamIdpType.GenericOidc, o => o.Authority = "https://z"),
            Idp("a", UpstreamIdpType.GenericOidc, o => { o.Authority = "https://a"; o.SortOrder = 10; }),
            Idp("y", UpstreamIdpType.GenericOidc, o => o.Authority = "https://y")]);
        Assert.Equal(["z", "y", "a", "c"], reg.Enabled.Select(i => i.Id));
    }

    [Fact]
    public void FromConfiguration_BindsSortOrder()
    {
        var reg = UpstreamIdpRegistry.FromConfiguration(Config(new Dictionary<string, string?>
        {
            ["UpstreamIdps:first:Type"] = "GenericOidc", ["UpstreamIdps:first:DisplayName"] = "First", ["UpstreamIdps:first:ClientId"] = "c", ["UpstreamIdps:first:Authority"] = "https://f.example", ["UpstreamIdps:first:SortOrder"] = "2",
            ["UpstreamIdps:second:Type"] = "GenericOidc", ["UpstreamIdps:second:DisplayName"] = "Second", ["UpstreamIdps:second:ClientId"] = "c", ["UpstreamIdps:second:Authority"] = "https://s.example", ["UpstreamIdps:second:SortOrder"] = "1"
        }));
        Assert.Equal(["second", "first"], reg.Enabled.Select(i => i.Id));
    }

    [Fact]
    public void FromConfiguration_RejectsLegacyArray() =>
        Assert.Throws<InvalidOperationException>(() => UpstreamIdpRegistry.FromConfiguration(Config(
            new Dictionary<string, string?> { ["UpstreamIdps:0:Id"] = "a", ["UpstreamIdps:0:Type"] = "GenericOidc" })));
}
