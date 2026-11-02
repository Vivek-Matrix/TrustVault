using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TrustVault.Api.Models.Auth;
using TrustVault.Api.Services;

namespace TrustVault.Api.Controllers;

[ApiController]
[Route("api/auth")]
[EnableRateLimiting("authentication")]
[AllowAnonymous]
public sealed class AuthController(IAuthenticationService authenticationService) : ControllerBase
{
    /// <summary>Creates a Viewer account and returns its initial access token.</summary>
    [HttpPost("register")]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Register(
        RegisterRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authenticationService.RegisterAsync(request, cancellationToken);
        if (result.EmailAlreadyExists)
        {
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "An account with this email already exists."
            });
        }

        if (result.Authentication is null)
        {
            return BadRequest(new ValidationProblemDetails(
                new Dictionary<string, string[]>(result.Errors))
            {
                Status = StatusCodes.Status400BadRequest
            });
        }

        return Created("/api/system/whoami", result.Authentication);
    }

    /// <summary>Authenticates an account and returns a short-lived access token.</summary>
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
}
