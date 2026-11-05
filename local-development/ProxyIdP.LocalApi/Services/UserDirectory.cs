using ProxyIdP.LocalApi.Models;

namespace ProxyIdP.LocalApi.Services;

/// <summary>Deterministic fake user data.</summary>
public sealed class UserDirectory
{
    private static readonly string[] Departments = ["Engineering", "Sales", "Finance", "Support"];

    public UserProfile GetProfile(string email)
    {
        var local = email.Split('@')[0];
        var hash = (uint)email.ToLowerInvariant().Aggregate(17, (h, c) => h * 31 + c);
        var name = local.Length == 0 ? local : char.ToUpperInvariant(local[0]) + local[1..];
        return new UserProfile(email, name, Departments[hash % 4], $"E{hash % 100000:D5}");
    }

    public UserRoles GetRoles(string email) =>
        new(email, email.StartsWith("admin@", StringComparison.OrdinalIgnoreCase) ? ["admin", "user"] : ["user"]);
}
