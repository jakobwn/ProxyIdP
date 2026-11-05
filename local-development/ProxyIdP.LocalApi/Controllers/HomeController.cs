using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ProxyIdP.LocalApi.Controllers;

[ApiController]
[AllowAnonymous]
public class HomeController : ControllerBase
{
    [HttpGet("/")]
    public string Index() => "ProxyIdP local API";
}
