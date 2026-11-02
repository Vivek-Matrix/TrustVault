using Microsoft.EntityFrameworkCore;
using TrustVault.Infrastructure.Persistence;

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__TrustVault")
    ?? Environment.GetEnvironmentVariable("DATABASE_URL");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "ConnectionStrings:TrustVault or DATABASE_URL must be configured.");
}

connectionString = DatabaseConnectionStrings.Normalize(connectionString);
var options = new DbContextOptionsBuilder<ApplicationDbContext>()
    .UseNpgsql(connectionString)
    .Options;

await using var database = new ApplicationDbContext(options);
await database.Database.MigrateAsync();
Console.WriteLine("TrustVault database migrations applied.");
