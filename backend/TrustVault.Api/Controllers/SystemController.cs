using System.Data.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TrustVault.Application.Documents;
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
        [FromServices] IPrivateDocumentScanner scanner,
        ILogger<SystemController> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var canConnect = await database.Database.CanConnectAsync(cancellationToken);
            if (!canConnect)
            {
                return StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    new { status = "not_ready" });
            }

            await scanner.CheckReadinessAsync(cancellationToken);
            return Ok(new { status = "ready" });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Readiness check timed out.");
        }
        catch (DbException exception)
        {
            logger.LogWarning(exception, "Readiness database check failed.");
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Readiness scanner check failed.");
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(exception, "Readiness check failed.");
        }

        return StatusCode(
            StatusCodes.Status503ServiceUnavailable,
            new { status = "not_ready" });
    }

    [HttpGet("whoami")]
    public IActionResult WhoAmI() => Ok(new
    {
        userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
        roles = User.FindAll(System.Security.Claims.ClaimTypes.Role).Select(claim => claim.Value)
    });
}
