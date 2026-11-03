namespace TrustVault.Api.Models.Documents;

public sealed record DocumentGrantResponse(
    Guid UserId,
    string Email,
    string Permission,
    DateTime GrantedAtUtc);
