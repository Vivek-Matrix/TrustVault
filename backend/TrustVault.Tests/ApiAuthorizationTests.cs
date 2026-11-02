using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using TrustVault.Api.Models.Auth;
using TrustVault.Domain.Security;
using TrustVault.Infrastructure.Persistence.Entities;

namespace TrustVault.Tests;

public sealed class ApiAuthorizationTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client;
    private readonly ApiFactory _factory;

    public ApiAuthorizationTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_endpoint_is_anonymous()
    {
        var response = await _client.GetAsync("/api/system/health");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_endpoint_rejects_anonymous_requests()
    {
        var response = await _client.GetAsync("/api/system/ready");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_endpoint_accepts_valid_token_and_reports_database_state()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/system/ready");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CreateAccessToken());

        var response = await _client.SendAsync(request);

        Assert.Contains(
            response.StatusCode,
            new[]
            {
                System.Net.HttpStatusCode.OK,
                System.Net.HttpStatusCode.ServiceUnavailable
            });
    }

    [Fact]
    public async Task Unknown_api_route_returns_not_found_instead_of_spa_fallback()
    {
        var response = await _client.GetAsync("/api/unknown-route");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_rejects_anonymous_requests()
    {
        var response = await _client.GetAsync("/api/system/whoami");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_accepts_a_valid_access_token()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/system/whoami");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CreateAccessToken());

        var response = await _client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("WrongIssuer", "TrustVault.Tests", 5)]
    [InlineData("TrustVault.Tests", "WrongAudience", 5)]
    [InlineData("TrustVault.Tests", "TrustVault.Tests", -5)]
    public async Task Protected_endpoint_rejects_invalid_access_tokens(
        string issuer,
        string audience,
        int expiryMinutes)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/system/whoami");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            CreateAccessToken(issuer, audience, DateTime.UtcNow.AddMinutes(expiryMinutes)));

        var response = await _client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Registration_creates_viewer_and_returns_short_lived_access_token()
    {
        var email = NewEmail();
        using var response = await RegisterAsync(email);

        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(auth);
        Assert.Equal("Bearer", auth.TokenType);
        Assert.Contains(UserRoles.Viewer, auth.Roles);
        Assert.InRange(
            auth.ExpiresAtUtc,
            DateTimeOffset.UtcNow.AddMinutes(4),
            DateTimeOffset.UtcNow.AddMinutes(16));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/system/whoami");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.AccessToken);
        using var whoAmI = await _client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.OK, whoAmI.StatusCode);
    }

    [Fact]
    public async Task Registration_cannot_select_an_elevated_role()
    {
        var email = NewEmail();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new
            {
                email,
                password = TestPassword,
                role = UserRoles.Administrator
            })
        };
        using var response = await _client.SendAsync(request);
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();

        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(auth);
        Assert.Equal(new[] { UserRoles.Viewer }, auth.Roles);
    }

    [Fact]
    public async Task Registration_rejects_duplicate_email()
    {
        var email = NewEmail();
        using var created = await RegisterAsync(email);
        using var duplicate = await RegisterAsync(email);

        Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Login_returns_token_for_valid_credentials()
    {
        var email = NewEmail();
        using var registration = await RegisterAsync(email);
        using var response = await LoginAsync(email, TestPassword);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(auth);
        Assert.Equal(email, auth.Email);
    }

    [Fact]
    public async Task Login_uses_same_generic_error_for_unknown_email_and_wrong_password()
    {
        using var unknownEmail = await LoginAsync(NewEmail(), "WrongPassword123!");
        var email = NewEmail();
        using var registration = await RegisterAsync(email);
        using var wrongPassword = await LoginAsync(email, "WrongPassword123!");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, unknownEmail.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        var unknownProblem = await unknownEmail.Content.ReadFromJsonAsync<JsonElement>();
        var wrongPasswordProblem = await wrongPassword.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            unknownProblem.GetProperty("title").GetString(),
            wrongPasswordProblem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Login_locks_account_after_repeated_failed_passwords()
    {
        var email = NewEmail();
        using var registration = await RegisterAsync(email);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var invalid = await LoginAsync(email, "WrongPassword123!");
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, invalid.StatusCode);
        }

        using var locked = await LoginAsync(email, TestPassword);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, locked.StatusCode);
    }

    [Fact]
    public async Task Login_rejects_suspended_account()
    {
        var email = NewEmail();
        using var registration = await RegisterAsync(email);
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);
        user.IsSuspended = true;
        var updateResult = await userManager.UpdateAsync(user);
        Assert.True(updateResult.Succeeded);

        using var response = await LoginAsync(email, TestPassword);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_registration_password_is_rejected()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new RegisterRequest
            {
                Email = NewEmail(),
                Password = "weak"
            })
        };
        using var response = await _client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    private const string TestPassword = "ValidPassword123!";

    private async Task<HttpResponseMessage> RegisterAsync(string email)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new
            {
                email,
                password = TestPassword
            })
        };
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> LoginAsync(string email, string password)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new LoginRequest
            {
                Email = email,
                Password = password
            })
        };
        return await _client.SendAsync(request);
    }

    private static string NewEmail() => $"user-{Guid.NewGuid():N}@example.test";

    private string CreateAccessToken(
        string issuer = "TrustVault.Tests",
        string audience = "TrustVault.Tests",
        DateTime? expires = null)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ApiFactory.SigningKey));
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())],
            expires: expires ?? DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string SigningKey = "trustvault-test-signing-key-32-bytes-minimum";
    private readonly Dictionary<string, string?> _previousEnvironmentValues;

    public ApiFactory()
    {
        var testSettings = new Dictionary<string, string>
        {
            ["ConnectionStrings__TrustVault"] =
                "Host=localhost;Database=trustvault;Username=test;Password=test",
            ["Jwt__SigningKey"] = SigningKey,
            ["Jwt__Issuer"] = "TrustVault.Tests",
            ["Jwt__Audience"] = "TrustVault.Tests"
        };
        testSettings["ConnectionStrings__TrustVault"] =
            "Host=localhost;Port=55432;Database=trustvault;Username=trustvault_app;Password=local_only";
        testSettings["Jwt__AccessTokenLifetimeMinutes"] = "15";
        _previousEnvironmentValues = testSettings.Keys.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable);

        foreach (var setting in testSettings)
        {
            Environment.SetEnvironmentVariable(setting.Key, setting.Value);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        foreach (var setting in _previousEnvironmentValues)
        {
            Environment.SetEnvironmentVariable(setting.Key, setting.Value);
        }
    }
}
