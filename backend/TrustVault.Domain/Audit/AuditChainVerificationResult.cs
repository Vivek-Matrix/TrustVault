namespace TrustVault.Domain.Audit;

public sealed record AuditChainVerificationResult(
    bool IsValid,
    int EventCount,
    Guid? InvalidEventId,
    string? Error);
