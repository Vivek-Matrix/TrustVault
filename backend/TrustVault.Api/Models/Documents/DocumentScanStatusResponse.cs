namespace TrustVault.Api.Models.Documents;

public sealed record DocumentScanStatusResponse(
    string ScanStatus,
    int ScanAttemptCount,
    DateTime? LastScanAtUtc);
