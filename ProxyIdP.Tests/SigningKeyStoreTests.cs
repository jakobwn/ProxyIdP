using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ProxyIdP.Data;
using ProxyIdP.Options;

namespace ProxyIdP.Tests;

public class SigningKeyStoreTests : IDisposable
{
    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly FakeTime _time = new(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly ServiceProvider _sp;
    private readonly SigningKeyStore _store;
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"keys-{Guid.NewGuid():N}.db");

    public SigningKeyStoreTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<ProxyIdPDbContext>(o => o.UseSqlite($"Data Source={_db}"));
        services.AddDataProtection();
        _sp = services.BuildServiceProvider();
        using (var scope = _sp.CreateScope())
            scope.ServiceProvider.GetRequiredService<ProxyIdPDbContext>().Database.EnsureCreated();
        _store = new SigningKeyStore(_sp.GetRequiredService<IServiceScopeFactory>(), _sp.GetRequiredService<IDataProtectionProvider>(),
            Microsoft.Extensions.Options.Options.Create(new SigningKeyOptions()), _time);
    }

    private async Task<List<SigningKey>> AllKeys()
    {
        using var scope = _sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ProxyIdPDbContext>().SigningKeys.AsNoTracking().ToListAsync();
    }

    private static async Task<string> Sign(SigningKeyStore store, DateTimeOffset now)
    {
        var creds = await store.GetSigningCredentialsAsync();
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "i", Audience = "a", NotBefore = now.UtcDateTime, Expires = now.UtcDateTime.AddMinutes(30), SigningCredentials = creds
        });
    }

    [Fact]
    public async Task FreshStore_HasTwoValidKeys_With30DayLifetime()
    {
        await _store.EnsureKeysAsync();
        var keys = await AllKeys();
        Assert.Equal(2, keys.Count);
        Assert.All(keys, k => Assert.Equal(TimeSpan.FromDays(30), k.ExpiresAt - k.CreatedAt));
        Assert.Equal(2, (await _store.GetValidationKeysAsync()).Count);
    }

    [Fact]
    public async Task OnlyNewestKey_Signs()
    {
        await _store.EnsureKeysAsync();
        var newest = (await AllKeys()).OrderByDescending(k => k.CreatedAt).First();
        var jwt = new JsonWebToken(await Sign(_store, _time.Now));
        Assert.Equal(newest.Kid, jwt.Kid);
    }

    [Fact]
    public async Task EnsureKeys_Twice_DoesNotRotateEarly()
    {
        await _store.EnsureKeysAsync();
        _time.Now = _time.Now.AddDays(13);
        await _store.EnsureKeysAsync();
        Assert.Equal(2, (await AllKeys()).Count);
    }

    [Fact]
    public async Task Rotation_After14Days_AddsNewSigningKey_AndOldTokensStillValidate()
    {
        await _store.EnsureKeysAsync();
        var oldToken = await Sign(_store, _time.Now);
        var oldKid = new JsonWebToken(oldToken).Kid;

        _time.Now = _time.Now.AddDays(14);
        await _store.EnsureKeysAsync();

        var keys = await AllKeys();
        Assert.Equal(3, keys.Count);
        var newToken = await Sign(_store, _time.Now);
        Assert.NotEqual(oldKid, new JsonWebToken(newToken).Kid);

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(oldToken, new TokenValidationParameters
        {
            ValidIssuer = "i", ValidAudience = "a", ValidateLifetime = false, IssuerSigningKeys = await _store.GetValidationKeysAsync()
        });
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ExpiredKeys_AreRemoved_AndMinimumTwoKeysRestoredAfterLongDowntime()
    {
        await _store.EnsureKeysAsync();
        _time.Now = _time.Now.AddDays(200);
        await _store.EnsureKeysAsync();

        var keys = await AllKeys();
        var now = _time.Now.UtcDateTime;
        Assert.Equal(2, keys.Count);
        Assert.All(keys, k => Assert.True(k.ExpiresAt > now));
        var newest = keys.OrderByDescending(k => k.CreatedAt).First();
        Assert.Equal(now, newest.CreatedAt);
        Assert.Equal(new JsonWebToken(await Sign(_store, _time.Now)).Kid, newest.Kid);
    }

    public void Dispose()
    {
        _sp.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_db); } catch { }
    }
}
