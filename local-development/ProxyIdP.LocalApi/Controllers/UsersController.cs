using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProxyIdP.LocalApi.Models;
using ProxyIdP.LocalApi.Services;

namespace ProxyIdP.LocalApi.Controllers;

/// <summary>Endpoints for plugins to call.</summary>
[ApiController]
[Authorize(Roles = "plugin")]
[Route("users/{email}")]
public class UsersController(UserDirectory directory) : ControllerBase
{
    [HttpGet("profile")]
    public UserProfile Profile(string email) => directory.GetProfile(email);

    [HttpGet("roles")]
    public UserRoles Roles(string email) => directory.GetRoles(email);
}
