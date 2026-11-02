using System.ComponentModel.DataAnnotations;

namespace TrustVault.Api.Models.Auth;

/// <summary>Credentials and email address for creating a TrustVault account.</summary>
public sealed record RegisterRequest
{
    /// <summary>The account email address.</summary>
    [Required, EmailAddress, MaxLength(256)]
    public required string Email { get; init; }

    /// <summary>A password that satisfies the configured Identity password policy.</summary>
    [Required, MinLength(12), MaxLength(128)]
    public required string Password { get; init; }
}
