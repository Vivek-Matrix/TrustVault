using System.ComponentModel.DataAnnotations;

namespace TrustVault.Api.Models.Auth;

public sealed record ChangePasswordRequest
{
    [Required, MaxLength(128)]
    public required string CurrentPassword { get; init; }

    [Required, MinLength(12), MaxLength(128)]
    public required string NewPassword { get; init; }
}
