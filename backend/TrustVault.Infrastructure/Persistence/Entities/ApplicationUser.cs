using Microsoft.AspNetCore.Identity;

namespace TrustVault.Infrastructure.Persistence.Entities;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public bool IsSuspended { get; set; }
    public bool IsPrimaryAdministrator { get; set; }
    public bool MustChangePassword { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? RefreshTokenHash { get; set; }
    public DateTimeOffset? RefreshTokenExpiresAtUtc { get; set; }
    public DateTimeOffset? RefreshTokenIssuedAtUtc { get; set; }
}
