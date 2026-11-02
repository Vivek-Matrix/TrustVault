using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;

namespace TrustVault.Tests;

public sealed class ApiAuthorizationTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client;

    public ApiAuthorizationTests(ApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_endpoint_is_anonymous()
    {
        var response = await _client.GetAsync("/api/system/health");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
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
