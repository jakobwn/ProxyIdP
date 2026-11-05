namespace ProxyIdP.LocalApi.Models;

public sealed record AuditEntry(string? ClientId, string? Subject, string? Idp);

public sealed record ResolveRequest(string? Email);

public sealed record ResolveResponse(string IdpId);

public sealed record UserProfile(string Email, string DisplayName, string Department, string EmployeeId);

public sealed record UserRoles(string Email, string[] Roles);
