using Microsoft.AspNetCore.Identity;

namespace TrustVault.Infrastructure.Persistence.Entities;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public bool IsSuspended { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
