namespace TrustVault.Api.Models.Documents;

public sealed record DocumentResponse(
    Guid Id,
    string Title,
    string? Description,
    string Category,
    string OriginalFileName,
    long SizeBytes,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    bool IsOwner,
    string? Permission,
    string ScanStatus,
    int ScanAttemptCount,
    DateTime? LastScanAtUtc);
