namespace TrustVault.Api.Models.Admin;

public sealed record MemberResponse(
    string UserId,
    string Email,
    IReadOnlyList<string> Roles,
    bool IsSuspended,
    DateTime CreatedAtUtc);
