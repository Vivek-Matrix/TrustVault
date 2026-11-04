using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TrustVault.Api.Models.Auth;
using TrustVault.Api.Services;

namespace TrustVault.Api.Controllers;

[ApiController]
[Route("api/auth")]
[EnableRateLimiting("authentication")]
[Authorize]
public sealed class AuthController(IAuthenticationService authenticationService) : ControllerBase
{
    /// <summary>Authenticates an account and returns a short-lived access token.</summary>
    [AllowAnonymous]
    [HttpPost("login")]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthResponse>> Login(
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        var response = await authenticationService.LoginAsync(request, cancellationToken);
        if (response is null)
        {
            return Unauthorized(new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Invalid email or password."
            });
        }

        return Ok(response);
    }

    [HttpPost("change-password")]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ChangePassword(
        ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized();
        }

        var result = await authenticationService.ChangePasswordAsync(
            userId,
            request,
            cancellationToken);
        if (result.Authentication is null)
        {
            if (result.Errors.Count == 0)
            {
                return Unauthorized();
            }

            return BadRequest(new ValidationProblemDetails(
                new Dictionary<string, string[]>(result.Errors))
            {
                Status = StatusCodes.Status400BadRequest
            });
        }

        return Ok(result.Authentication);
    }

    [AllowAnonymous]
    [HttpPost("refresh")]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthResponse>> Refresh(
        [FromBody] RefreshRequest request,
        CancellationToken cancellationToken)
    {
        var response = await authenticationService.RefreshAsync(request.RefreshToken, cancellationToken);
        if (response is null)
        {
            return Unauthorized(new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Refresh token is invalid or expired."
            });
        }

        return Ok(response);
    }

    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized();
        }

        await authenticationService.RevokeRefreshTokenAsync(userId, cancellationToken);
        return NoContent();
    }
}
