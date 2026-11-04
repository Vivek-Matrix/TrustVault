namespace TrustVault.Api.Models.Documents;

public sealed record DocumentAuditResponse(
    Guid Id,
    string? ActorEmail,
    string Activity,
    string Result,
    DateTime TimestampUtc);
