using TrustVault.Api.Models.Auth;

namespace TrustVault.Api.Services;

public interface IAuthenticationService
{
    Task<RegistrationResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken);

    Task<AuthResponse?> LoginAsync(LoginRequest request, CancellationToken cancellationToken);

    Task<AuthResponse?> RefreshAsync(string refreshToken, CancellationToken cancellationToken);

    Task RevokeRefreshTokenAsync(Guid userId, CancellationToken cancellationToken);
}

public sealed record RegistrationResult(
    AuthResponse? Authentication,
    IReadOnlyDictionary<string, string[]> Errors,
    bool EmailAlreadyExists);
