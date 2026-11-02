using Microsoft.EntityFrameworkCore;
using TrustVault.Domain.Audit;
using TrustVault.Infrastructure.Persistence;

namespace TrustVault.Tests;

public sealed class AuditEventImmutabilityTests
{
    [Fact]
    public async Task Modified_audit_event_is_rejected_by_sync_and_async_saves()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=trustvault;Username=test;Password=test")
            .Options;

        using var syncContext = new ApplicationDbContext(options);
        syncContext.AuditEvents.Attach(new AuditEvent { Id = Guid.NewGuid() });
        syncContext.Entry(syncContext.AuditEvents.Local.Single()).State = EntityState.Modified;
        Assert.Throws<InvalidOperationException>(() => syncContext.SaveChanges());

        await using var asyncContext = new ApplicationDbContext(options);
        asyncContext.AuditEvents.Attach(new AuditEvent { Id = Guid.NewGuid() });
        asyncContext.Entry(asyncContext.AuditEvents.Local.Single()).State = EntityState.Deleted;
        await Assert.ThrowsAsync<InvalidOperationException>(() => asyncContext.SaveChangesAsync());
    }
}
