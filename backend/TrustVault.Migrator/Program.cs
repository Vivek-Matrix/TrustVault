using Microsoft.EntityFrameworkCore;
using TrustVault.Domain.Audit;
using TrustVault.Infrastructure.Persistence;

var verifyAudit = args.Length == 1 && args[0] == "--verify-audit";
if (args.Length > 0 && !verifyAudit)
{
    throw new ArgumentException("The only supported argument is --verify-audit.");
}

var connectionString =
    Environment.GetEnvironmentVariable("ConnectionStrings__TrustVaultMigrator");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "ConnectionStrings:TrustVaultMigrator must be configured.");
}

connectionString = DatabaseConnectionStrings.Normalize(connectionString);
var options = new DbContextOptionsBuilder<ApplicationDbContext>()
    .UseNpgsql(connectionString)
    .Options;

await using var database = new ApplicationDbContext(options);
if (verifyAudit)
{
    var events = await database.AuditEvents
        .AsNoTracking()
        .OrderBy(auditEvent => auditEvent.TimestampUtc)
        .ThenBy(auditEvent => auditEvent.Id)
        .ToListAsync();
    var result = AuditChainVerifier.Verify(events);
    if (result.IsValid)
    {
        Console.WriteLine($"Audit chain verified: {result.EventCount} events.");
        return 0;
    }

    Console.Error.WriteLine(
        $"Audit chain verification failed at event {result.InvalidEventId} "
        + $"(position {result.EventCount}): {result.Error}");
    return 1;
}

await database.Database.MigrateAsync();
Console.WriteLine("TrustVault database migrations applied.");
return 0;
