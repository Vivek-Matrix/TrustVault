using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace TrustVault.Api.Controllers;

[ApiController]
[Route("api/system")]
public sealed class SystemController : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("health")]
    public IActionResult Health() => Ok(new { status = "ok" });

    [HttpGet("whoami")]
    public IActionResult WhoAmI() => Ok(new
    {
        userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
        roles = User.FindAll(System.Security.Claims.ClaimTypes.Role).Select(claim => claim.Value)
    });
}
