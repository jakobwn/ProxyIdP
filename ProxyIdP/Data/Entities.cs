using Microsoft.EntityFrameworkCore;

namespace ProxyIdP.Data;

public class Client
{
    public int Id { get; set; }
    public string ClientId { get; set; } = "";
    public string? SecretHash { get; set; }
    public List<string> RedirectUris { get; set; } = [];
    public List<string> PostLogoutRedirectUris { get; set; } = [];
    public List<string> AllowedScopes { get; set; } = ["openid", "profile", "email"];
}

public class SigningKey
{
    public int Id { get; set; }
    public string Kid { get; set; } = "";
    public string ProtectedPrivateKey { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public class AuthorizationGrant
{
    public int Id { get; set; }
    public string CodeHash { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string ClaimsJson { get; set; } = "[]";
    public string? Nonce { get; set; }
    public string CodeChallenge { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public string Scope { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
}

public class PendingAuthorization
{
    public string Id { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public string Scope { get; set; } = "";
    public string? State { get; set; }
    public string? Nonce { get; set; }
    public string CodeChallenge { get; set; } = "";
    public string? IdpId { get; set; }
    public string? UpstreamState { get; set; }
    public string? UpstreamNonce { get; set; }
    public string? UpstreamCodeVerifier { get; set; }
    public string? LoginHint { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public class ProxyIdPDbContext(DbContextOptions<ProxyIdPDbContext> options) : DbContext(options)
{
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<SigningKey> SigningKeys => Set<SigningKey>();
    public DbSet<AuthorizationGrant> Grants => Set<AuthorizationGrant>();
    public DbSet<PendingAuthorization> PendingAuthorizations => Set<PendingAuthorization>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Client>().HasIndex(c => c.ClientId).IsUnique();
        b.Entity<SigningKey>().HasIndex(k => k.Kid).IsUnique();
        b.Entity<AuthorizationGrant>().HasIndex(g => g.CodeHash).IsUnique();
    }
}
