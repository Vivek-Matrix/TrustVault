namespace TrustVault.Domain.Audit;

public static class AuditChainVerifier
{
    public static AuditChainVerificationResult Verify(IEnumerable<AuditEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        string? expectedPreviousHash = null;
        var eventCount = 0;
        foreach (var auditEvent in events)
        {
            if (!string.Equals(
                auditEvent.PreviousHash,
                expectedPreviousHash,
                StringComparison.Ordinal))
            {
                return new AuditChainVerificationResult(
                    false,
                    eventCount + 1,
                    auditEvent.Id,
                    "Previous hash does not match the preceding event.");
            }

            if (!IsValidRecordHash(auditEvent))
            {
                return new AuditChainVerificationResult(
                    false,
                    eventCount + 1,
                    auditEvent.Id,
                    "Record hash does not match the event contents.");
            }

            expectedPreviousHash = auditEvent.RecordHash;
            eventCount++;
        }

        return new AuditChainVerificationResult(true, eventCount, null, null);
    }

    private static bool IsValidRecordHash(AuditEvent auditEvent) =>
        string.Equals(
            auditEvent.RecordHash,
            AuditEventHash.Calculate(auditEvent),
            StringComparison.Ordinal)
        || string.Equals(
            auditEvent.RecordHash,
            AuditEventHash.CalculateLegacy(auditEvent),
            StringComparison.Ordinal);
}
