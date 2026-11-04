using TrustVault.Api.Models.Auth;

namespace TrustVault.Api.Services;

public interface IAuthenticationService
{
    Task<AuthResponse?> LoginAsync(LoginRequest request, CancellationToken cancellationToken);

    Task<AuthResponse?> RefreshAsync(string refreshToken, CancellationToken cancellationToken);

    Task<(AuthResponse? Authentication, IReadOnlyDictionary<string, string[]> Errors)> ChangePasswordAsync(
        Guid userId,
        ChangePasswordRequest request,
        CancellationToken cancellationToken);

    Task RevokeRefreshTokenAsync(Guid userId, CancellationToken cancellationToken);
}
