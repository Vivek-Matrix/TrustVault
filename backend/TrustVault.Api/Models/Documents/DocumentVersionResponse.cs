namespace TrustVault.Api.Models.Documents;

public sealed record DocumentVersionResponse(
    int VersionNumber,
    string OriginalFileName,
    long SizeBytes,
    DateTime CreatedAtUtc);
