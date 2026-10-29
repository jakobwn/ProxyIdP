using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProxyIdP.Data;
using ProxyIdP.Options;
using ProxyIdP.Services;

namespace ProxyIdP.Tests;

public class ClientConfigTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static async Task<(ProxyIdPDbContext Db, SqliteConnection Conn)> NewDbAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var db = new ProxyIdPDbContext(new DbContextOptionsBuilder<ProxyIdPDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();
        return (db, conn);
    }

    [Fact]
    public void FromConfiguration_UsesKeyAsClientId()
    {
        var clients = ClientOptions.FromConfiguration(Config(new()
        {
            ["Clients:app1:RedirectUris:0"] = "https://a/cb",
            ["Clients:app1:PostLogoutRedirectUris:0"] = "https://a/out"
        }));
        var c = Assert.Single(clients);
        Assert.Equal("app1", c.ClientId);
        Assert.Equal(["https://a/cb"], c.RedirectUris);
    }

    [Fact]
    public void FromConfiguration_EmptySection_ReturnsNone() =>
        Assert.Empty(ClientOptions.FromConfiguration(Config(new())));

    [Fact]
    public void FromConfiguration_RejectsArrayForm() =>
        Assert.Throws<InvalidOperationException>(() => ClientOptions.FromConfiguration(Config(new()
        {
            ["Clients:0:RedirectUris:0"] = "https://a/cb"
        })));

    [Fact]
    public void FromConfiguration_RejectsMissingRedirectUris() =>
        Assert.Throws<InvalidOperationException>(() => ClientOptions.FromConfiguration(Config(new()
        {
            ["Clients:app1:Secret"] = "s"
        })));

    [Fact]
    public async Task Seed_InsertsThenUpdates_AndLeavesOthersAlone()
    {
        var (db, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = db;
        db.Clients.Add(new Client { ClientId = "other", RedirectUris = ["https://o/cb"] });
        await db.SaveChangesAsync();

        await Program.SeedClientsAsync(db, [new ClientOptions { ClientId = "app1", RedirectUris = ["https://a/cb"] }]);
        var inserted = await db.Clients.SingleAsync(c => c.ClientId == "app1");
        Assert.Null(inserted.SecretHash);
        Assert.Equal(["openid", "profile", "email"], inserted.AllowedScopes);

        await Program.SeedClientsAsync(db, [new ClientOptions
        {
            ClientId = "app1", Secret = "s3cret", RedirectUris = ["https://b/cb"], AllowedScopes = ["openid"]
        }]);
        var updated = await db.Clients.SingleAsync(c => c.ClientId == "app1");
        Assert.Equal(Crypto.Sha256("s3cret"), updated.SecretHash);
        Assert.Equal(["https://b/cb"], updated.RedirectUris);
        Assert.Equal(["openid"], updated.AllowedScopes);

        Assert.Equal(["https://o/cb"], (await db.Clients.SingleAsync(c => c.ClientId == "other")).RedirectUris);
    }
}
