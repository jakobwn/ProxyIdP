using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProxyIdP.LocalApi.Models;
using ProxyIdP.LocalApi.Services;

namespace ProxyIdP.LocalApi.Controllers;

/// <summary>IdP resolver contract used by ProxyIdP: { "email": "..." } -> { "idpId": "..." }.</summary>
[ApiController]
[Authorize(Roles = "resolver")]
[Route("resolve")]
public class ResolveController(IdpMapper mapper) : ControllerBase
{
    [HttpGet]
    public IActionResult Get([FromQuery] string? email) => Resolve(email);

    [HttpPost]
    public IActionResult Post([FromBody] ResolveRequest? body, [FromQuery] string? email) => Resolve(email ?? body?.Email);

    private IActionResult Resolve(string? email)
    {
        var idp = mapper.Resolve(email);
        return idp is null ? NotFound() : Ok(new ResolveResponse(idp));
    }
}
