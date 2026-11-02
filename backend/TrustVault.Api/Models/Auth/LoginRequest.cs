using System.ComponentModel.DataAnnotations;

namespace TrustVault.Api.Models.Auth;

/// <summary>Credentials for signing in to a TrustVault account.</summary>
public sealed record LoginRequest
{
    /// <summary>The account email address.</summary>
    [Required, EmailAddress, MaxLength(256)]
    public required string Email { get; init; }

    /// <summary>The account password.</summary>
    [Required, MaxLength(128)]
    public required string Password { get; init; }
}
