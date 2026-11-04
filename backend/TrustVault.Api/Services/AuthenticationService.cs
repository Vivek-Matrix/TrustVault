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
    private readonly int _refreshTokenLifetimeDays =
        int.TryParse(configuration["Jwt:RefreshTokenLifetimeDays"], out var configuredRefreshLifetime)
            ? configuredRefreshLifetime
            : 7;
    private readonly ApplicationUser _timingUser = new();
    private readonly string _dummyPasswordHash = userManager.PasswordHasher.HashPassword(
        new ApplicationUser(),
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

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
        var authResponse = CreateAuthenticationResponse(user, roles);
        await SaveRefreshTokenAsync(user, authResponse.RefreshToken!, cancellationToken);
        return authResponse;
    }

    public async Task<(AuthResponse? Authentication, IReadOnlyDictionary<string, string[]> Errors)>
        ChangePasswordAsync(
            Guid userId,
            ChangePasswordRequest request,
            CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null || user.IsSuspended)
        {
            return (null, EmptyErrors());
        }

        var changeResult = await userManager.ChangePasswordAsync(
            user,
            request.CurrentPassword,
            request.NewPassword);
        if (!changeResult.Succeeded)
        {
            return (null, ValidationFailure(changeResult.Errors));
        }

        user.MustChangePassword = false;
        var updateResult = await userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            return (null, ValidationFailure(updateResult.Errors));
        }

        var roles = await userManager.GetRolesAsync(user);
        var authentication = CreateAuthenticationResponse(user, roles);
        await SaveRefreshTokenAsync(user, authentication.RefreshToken!, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (authentication, EmptyErrors());
    }

    public async Task<AuthResponse?> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var trimmedRefreshToken = refreshToken?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedRefreshToken))
        {
            return null;
        }

        var user = await database.Users
            .FirstOrDefaultAsync(user => user.RefreshTokenHash != null && user.RefreshTokenHash != string.Empty, cancellationToken);

        if (user is null)
        {
            return null;
        }

        var tokenHash = HashToken(trimmedRefreshToken);
        var candidate = await database.Users
            .FirstOrDefaultAsync(
                user => user.RefreshTokenHash == tokenHash
                    && user.RefreshTokenExpiresAtUtc != null
                    && user.RefreshTokenExpiresAtUtc > DateTimeOffset.UtcNow,
                cancellationToken);

        if (candidate is null || candidate.IsSuspended)
        {
            logger.LogWarning("Refresh token rejected for user with no valid active rotation token.");
            return null;
        }

        var roles = await userManager.GetRolesAsync(candidate);
        var authResponse = CreateAuthenticationResponse(candidate, roles);
        await SaveRefreshTokenAsync(candidate, authResponse.RefreshToken!, cancellationToken);
        return authResponse;
    }

    public async Task RevokeRefreshTokenAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await database.Users
            .SingleOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (user is null)
        {
            return;
        }

        user.RefreshTokenHash = null;
        user.RefreshTokenExpiresAtUtc = null;
        user.RefreshTokenIssuedAtUtc = null;
        user.SecurityStamp = Guid.NewGuid().ToString();
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task SaveRefreshTokenAsync(
        ApplicationUser user,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        user.RefreshTokenHash = HashToken(refreshToken);
        user.RefreshTokenIssuedAtUtc = now;
        user.RefreshTokenExpiresAtUtc = now.AddDays(_refreshTokenLifetimeDays);
        await database.SaveChangesAsync(cancellationToken);
    }

    private static string HashToken(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash);
    }

    private static string GenerateRefreshToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private AuthResponse CreateAuthenticationResponse(
        ApplicationUser user,
        IEnumerable<string> roles)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddMinutes(_accessTokenLifetimeMinutes);
        var refreshToken = GenerateRefreshToken();
        var refreshTokenExpiresAt = now.AddDays(_refreshTokenLifetimeDays);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName ?? user.Email ?? user.Id.ToString()),
            new(ClaimTypes.Email, user.Email ?? string.Empty),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("security_stamp", user.SecurityStamp ?? string.Empty)
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
            roles.ToArray(),
            refreshToken,
            refreshTokenExpiresAt,
            user.MustChangePassword);
    }

    private static IReadOnlyDictionary<string, string[]> ValidationFailure(
        IEnumerable<IdentityError> errors) =>
        errors
            .GroupBy(error => error.Code.StartsWith("Password", StringComparison.Ordinal)
                ? "password"
                : "account")
            .ToDictionary(
                group => group.Key,
                group => group.Select(error => error.Description).ToArray());

    private static IReadOnlyDictionary<string, string[]> EmptyErrors() =>
        new Dictionary<string, string[]>();
}
