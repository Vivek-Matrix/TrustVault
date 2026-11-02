using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using TrustVault.Api.Models.Auth;
using TrustVault.Domain.Security;
using TrustVault.Infrastructure.Persistence;
using TrustVault.Infrastructure.Persistence.Entities;

namespace TrustVault.Api.Services;

public sealed class AuthenticationService(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    RoleManager<IdentityRole<Guid>> roleManager,
    ApplicationDbContext database,
    IConfiguration configuration,
    ILogger<AuthenticationService> logger) : IAuthenticationService
{
    private const string TokenIssuer = "TrustVault";
    private const string TokenAudience = "TrustVault";

    private readonly string _issuer = configuration["Jwt:Issuer"] ?? TokenIssuer;
    private readonly string _audience = configuration["Jwt:Audience"] ?? TokenAudience;
    private readonly string _signingKey = configuration["Jwt:SigningKey"]
        ?? throw new InvalidOperationException("Jwt:SigningKey is required.");
    private readonly int _accessTokenLifetimeMinutes =
        int.TryParse(configuration["Jwt:AccessTokenLifetimeMinutes"], out var configuredLifetime)
            ? configuredLifetime
            : 15;
    private readonly ApplicationUser _timingUser = new();
    private readonly string _dummyPasswordHash = userManager.PasswordHasher.HashPassword(
        new ApplicationUser(),
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    public async Task<RegistrationResult> RegisterAsync(
        RegisterRequest request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);

        var email = request.Email.Trim();
        if (await userManager.FindByEmailAsync(email) is not null)
        {
            return EmailConflict();
        }

        await EnsureViewerRoleExistsAsync();

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            CreatedAtUtc = DateTime.UtcNow
        };
        var createResult = await userManager.CreateAsync(user, request.Password);
        if (!createResult.Succeeded)
        {
            if (createResult.Errors.Any(error =>
                    error.Code is nameof(IdentityErrorDescriber.DuplicateEmail)
                        or nameof(IdentityErrorDescriber.DuplicateUserName)))
            {
                return EmailConflict();
            }

            return ValidationFailure(createResult.Errors);
        }

        var roleResult = await userManager.AddToRoleAsync(user, UserRoles.Viewer);
        if (!roleResult.Succeeded)
        {
            return ValidationFailure(roleResult.Errors);
        }

        await transaction.CommitAsync(cancellationToken);
        var roles = await userManager.GetRolesAsync(user);
        return new RegistrationResult(CreateAuthenticationResponse(user, roles), EmptyErrors(), false);
    }

    public async Task<AuthResponse?> LoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var email = request.Email.Trim();
        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            userManager.PasswordHasher.VerifyHashedPassword(
                _timingUser,
                _dummyPasswordHash,
                request.Password);
            logger.LogWarning("Login failed for an unknown account.");
            return null;
        }

        var signInResult = await signInManager.CheckPasswordSignInAsync(
            user,
            request.Password,
            lockoutOnFailure: true);
        if (!signInResult.Succeeded || user.IsSuspended)
        {
            logger.LogWarning(
                "Login failed for account {UserId}; locked out: {IsLockedOut}; suspended: {IsSuspended}.",
                user.Id,
                signInResult.IsLockedOut,
                user.IsSuspended);
            return null;
        }

        var roles = await userManager.GetRolesAsync(user);
        return CreateAuthenticationResponse(user, roles);
    }

    private async Task EnsureViewerRoleExistsAsync()
    {
        if (await roleManager.RoleExistsAsync(UserRoles.Viewer))
        {
            return;
        }

        var result = await roleManager.CreateAsync(new IdentityRole<Guid>(UserRoles.Viewer));
        if (!result.Succeeded && !await roleManager.RoleExistsAsync(UserRoles.Viewer))
        {
            throw new InvalidOperationException(
                $"Unable to initialize the default user role: {string.Join("; ", result.Errors.Select(error => error.Code))}");
        }
    }

    private AuthResponse CreateAuthenticationResponse(
        ApplicationUser user,
        IEnumerable<string> roles)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddMinutes(_accessTokenLifetimeMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName ?? user.Email ?? user.Id.ToString()),
            new(ClaimTypes.Email, user.Email ?? string.Empty),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_signingKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: signingCredentials);
        var encodedToken = new JwtSecurityTokenHandler().WriteToken(token);

        return new AuthResponse(
            encodedToken,
            "Bearer",
            expiresAt,
            user.Id.ToString(),
            user.Email ?? string.Empty,
            roles.ToArray());
    }

    private static RegistrationResult EmailConflict() =>
        new(null, EmptyErrors(), true);

    private static RegistrationResult ValidationFailure(IEnumerable<IdentityError> errors) =>
        new(
            null,
            errors
                .GroupBy(error => error.Code.StartsWith("Password", StringComparison.Ordinal)
                    ? "password"
                    : "account")
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(error => error.Description).ToArray()),
            false);

    private static IReadOnlyDictionary<string, string[]> EmptyErrors() =>
        new Dictionary<string, string[]>();
}
