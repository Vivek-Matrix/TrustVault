namespace TrustVault.Api.Models.Auth;

public sealed class RefreshRequest
{
    public string RefreshToken { get; set; } = string.Empty;
}
