using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProxyIdP.LocalApi.Models;

namespace ProxyIdP.LocalApi.Controllers;

[ApiController]
[Authorize(Roles = "plugin")]
[Route("audit")]
public class AuditController(ILogger<AuditController> logger) : ControllerBase
{
    [HttpPost("token-issued")]
    public IActionResult TokenIssued(AuditEntry entry)
    {
        logger.LogInformation("Audit: token issued for {Subject} to client {ClientId} via {Idp}", entry.Subject, entry.ClientId, entry.Idp);
        return Accepted();
    }
}
