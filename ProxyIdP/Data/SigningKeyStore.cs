using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ProxyIdP.Options;

namespace ProxyIdP.Data;

public interface ISigningKeyStore
{
    Task EnsureKeysAsync(CancellationToken ct = default);
    Task<SigningCredentials> GetSigningCredentialsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SecurityKey>> GetValidationKeysAsync(CancellationToken ct = default);
    Task<object> GetJwksAsync(CancellationToken ct = default);
}

public sealed class SigningKeyStore(
    IServiceScopeFactory scopes,
    IDataProtectionProvider dataProtection,
    IOptions<SigningKeyOptions> options,
    TimeProvider time) : ISigningKeyStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly IDataProtector _protector = dataProtection.CreateProtector("ProxyIdP.SigningKeys");
    private readonly SigningKeyOptions _opt = options.Value;

    public async Task EnsureKeysAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProxyIdPDbContext>();
            var now = time.GetUtcNow().UtcDateTime;

            var expiredCutoff = now.AddDays(-_opt.ExpiredKeyGraceDays);
            var expired = await db.SigningKeys.Where(k => k.ExpiresAt < expiredCutoff).ToListAsync(ct);
            db.SigningKeys.RemoveRange(expired);

            var all = await db.SigningKeys.ToListAsync(ct);
            var undecryptable = all.Where(k => !CanUnprotect(k)).ToList();
            db.SigningKeys.RemoveRange(undecryptable.Except(expired));

            var valid = all.Where(k => k.ExpiresAt > now && !expired.Contains(k) && !undecryptable.Contains(k))
                .OrderByDescending(k => k.CreatedAt).ToList();

            var rotation = TimeSpan.FromDays(_opt.RotationIntervalDays);
            var lifetime = TimeSpan.FromDays(_opt.KeyLifetimeDays);

            if (valid.Count == 0 || now - valid[0].CreatedAt >= rotation)
            {
                db.SigningKeys.Add(CreateKey(now, lifetime));
                valid.Insert(0, null!);
            }

            // Ensure at least MinimumValidKeys; older fillers are backdated so they never become the signing key.
            var count = valid.Count;
            var step = 1;
            while (count < _opt.MinimumValidKeys)
            {
                db.SigningKeys.Add(CreateKey(now - rotation * step, lifetime));
                count++;
                step++;
            }

            await db.SaveChangesAsync(ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    private SigningKey CreateKey(DateTime createdAt, TimeSpan lifetime)
    {
        using var rsa = RSA.Create(2048);
        return new SigningKey
        {
            Kid = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(16)),
            ProtectedPrivateKey = _protector.Protect(Convert.ToBase64String(rsa.ExportPkcs8PrivateKey())),
            CreatedAt = createdAt,
            ExpiresAt = createdAt + lifetime
        };
    }

    private bool CanUnprotect(SigningKey k)
    {
        try { _protector.Unprotect(k.ProtectedPrivateKey); return true; }
        catch (CryptographicException) { return false; }
    }

    private RsaSecurityKey ToSecurityKey(SigningKey k)
    {
        var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(_protector.Unprotect(k.ProtectedPrivateKey)), out _);
        return new RsaSecurityKey(rsa) { KeyId = k.Kid };
    }

    private async Task<List<SigningKey>> LoadValidAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProxyIdPDbContext>();
        var now = time.GetUtcNow().UtcDateTime;
        var all = await db.SigningKeys.AsNoTracking().ToListAsync(ct);
        return all.Where(k => k.ExpiresAt > now && CanUnprotect(k)).OrderByDescending(k => k.CreatedAt).ToList();
    }

    public async Task<SigningCredentials> GetSigningCredentialsAsync(CancellationToken ct = default)
    {
        var keys = await LoadValidAsync(ct);
        if (keys.Count == 0)
        {
            await EnsureKeysAsync(ct);
            keys = await LoadValidAsync(ct);
        }
        return new SigningCredentials(ToSecurityKey(keys[0]), SecurityAlgorithms.RsaSha256);
    }

    public async Task<IReadOnlyList<SecurityKey>> GetValidationKeysAsync(CancellationToken ct = default)
    {
        var keys = await LoadValidAsync(ct);
        return keys.Select(k => (SecurityKey)ToSecurityKey(k)).ToList();
    }

    public async Task<object> GetJwksAsync(CancellationToken ct = default)
    {
        var keys = await LoadValidAsync(ct);
        var jwks = keys.Select(k =>
        {
            var p = ToSecurityKey(k).Rsa!.ExportParameters(false);
            return new Dictionary<string, string>
            {
                ["kty"] = "RSA",
                ["use"] = "sig",
                ["alg"] = "RS256",
                ["kid"] = k.Kid,
                ["n"] = Base64UrlEncoder.Encode(p.Modulus!),
                ["e"] = Base64UrlEncoder.Encode(p.Exponent!)
            };
        }).ToList();
        return new { keys = jwks };
    }
}

public sealed class SigningKeyRotationService(ISigningKeyStore store, ILogger<SigningKeyRotationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await store.EnsureKeysAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Signing key rotation failed");
            }
        }
    }
}
