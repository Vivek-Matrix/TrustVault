using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TrustVault.Domain.Audit;
using TrustVault.Infrastructure.Persistence;

namespace TrustVault.Api.Services;

public sealed class DocumentAuditWriter(ApplicationDbContext database)
{
    public async Task WriteAsync(
        HttpContext context,
        Guid? documentId,
        string action,
        AuditResult result,
        string metadataJson,
        CancellationToken cancellationToken)
    {
        var currentTransaction = database.Database.CurrentTransaction;
        var ownsTransaction = currentTransaction is null;
        IDbContextTransaction? transaction = null;
        if (ownsTransaction)
        {
            transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        }

        try
        {
            await database.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock(18348721, 9283147)",
                cancellationToken);

            var previous = await database.AuditEvents
                .OrderByDescending(auditEvent => auditEvent.TimestampUtc)
                .FirstOrDefaultAsync(cancellationToken);
            var currentUtcTicks = DateTime.UtcNow.Ticks;
            var timestamp = new DateTime(
                currentUtcTicks - currentUtcTicks % 10,
                DateTimeKind.Utc);
            if (previous is not null && timestamp <= previous.TimestampUtc)
            {
                timestamp = previous.TimestampUtc.AddTicks(10);
            }

            var userAgent = context.Request.Headers.UserAgent.ToString();
            var auditEvent = new AuditEvent
            {
                Id = Guid.NewGuid(),
                ActorUserId = Guid.TryParse(
                    context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
                    out var actorId) ? actorId : null,
                ActorRole = string.Join(
                    ",",
                    context.User.FindAll(System.Security.Claims.ClaimTypes.Role)
                        .Select(claim => claim.Value))
                    .Truncate(64),
                DocumentId = documentId,
                Action = action,
                Result = result,
                TimestampUtc = timestamp,
                RequestId = context.TraceIdentifier.Truncate(128),
                IpAddress = context.Connection.RemoteIpAddress?.ToString().TruncateNullable(45),
                UserAgent = userAgent.Truncate(512),
                MetadataJson = metadataJson,
                PreviousHash = previous?.RecordHash
            };
            auditEvent.RecordHash = AuditEventHash.Calculate(auditEvent);
            database.AuditEvents.Add(auditEvent);
            await database.SaveChangesAsync(cancellationToken);

            if (ownsTransaction)
            {
                await transaction!.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            if (ownsTransaction && transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            throw;
        }
        finally
        {
            if (ownsTransaction && transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

}

internal static class AuditStringExtensions
{
    public static string Truncate(this string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    public static string? TruncateNullable(this string? value, int maximumLength) =>
        value is null || value.Length <= maximumLength ? value : value[..maximumLength];
}
