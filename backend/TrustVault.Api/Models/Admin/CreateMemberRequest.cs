using System.ComponentModel.DataAnnotations;

namespace TrustVault.Api.Models.Admin;

public sealed record CreateMemberRequest
{
    [Required, EmailAddress, MaxLength(256)]
    public required string Email { get; init; }
}
