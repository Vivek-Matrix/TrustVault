namespace TrustVault.Api.Models.Auth;

/// <summary>Bearer credentials and the authenticated account identity.</summary>
public sealed record AuthResponse(
    string AccessToken,
    string TokenType,
    DateTimeOffset ExpiresAtUtc,
    string UserId,
    string Email,
    IReadOnlyList<string> Roles,
    string? RefreshToken = null,
    DateTimeOffset? RefreshTokenExpiresAtUtc = null,
    bool PasswordChangeRequired = false);
