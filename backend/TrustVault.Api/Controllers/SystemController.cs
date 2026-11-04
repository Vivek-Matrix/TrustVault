using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TrustVault.Infrastructure.Persistence;

namespace TrustVault.Api.Controllers;

[ApiController]
[Route("api/system")]
public sealed class SystemController : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("health")]
    public IActionResult Health() => Ok(new { status = "ok" });

    [AllowAnonymous]
    [HttpGet("ready")]
    public async Task<IActionResult> Ready(
        [FromServices] ApplicationDbContext database,
        CancellationToken cancellationToken)
    {
        var canConnect = await database.Database.CanConnectAsync(cancellationToken);
        return canConnect
            ? Ok(new { status = "ready" })
            : StatusCode(StatusCodes.Status503ServiceUnavailable, new { status = "not_ready" });
    }

    [HttpGet("whoami")]
    public IActionResult WhoAmI() => Ok(new
    {
        userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
        roles = User.FindAll(System.Security.Claims.ClaimTypes.Role).Select(claim => claim.Value)
    });
}
