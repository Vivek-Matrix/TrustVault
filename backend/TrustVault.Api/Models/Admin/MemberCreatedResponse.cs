namespace TrustVault.Api.Models.Admin;

public sealed record MemberCreatedResponse(
    string UserId,
    string Email,
    string Role,
    string TemporaryPassword,
    bool PasswordChangeRequired);
