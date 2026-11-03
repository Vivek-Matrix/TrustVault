using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using TrustVault.Api.Models.Auth;
using TrustVault.Api.Models.Documents;
using TrustVault.Domain.Security;
using TrustVault.Infrastructure.Persistence;
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

    [Fact]
    public async Task Document_endpoints_require_authentication()
    {
        using var list = await _client.GetAsync("/api/documents");
        using var metadata = await _client.GetAsync($"/api/documents/{Guid.NewGuid()}");
        using var download = await _client.GetAsync($"/api/documents/{Guid.NewGuid()}/content");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, metadata.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, download.StatusCode);
    }

    [Fact]
    public async Task Document_owner_can_upload_list_read_metadata_and_download()
    {
        var owner = await RegisterAndGetSessionAsync();
        using var upload = await UploadPdfAsync(owner);
        Assert.Equal(System.Net.HttpStatusCode.Created, upload.StatusCode);
        var document = await upload.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(document);

        using var listRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            "/api/documents",
            owner);
        using var list = await _client.SendAsync(listRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, list.StatusCode);
        var documents = await list.Content.ReadFromJsonAsync<DocumentResponse[]>();
        Assert.NotNull(documents);
        Assert.Contains(documents, item => item.Id == document.Id);

        using var metadataRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}",
            owner);
        using var metadata = await _client.SendAsync(metadataRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, metadata.StatusCode);

        using var downloadRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}/content",
            owner);
        using var download = await _client.SendAsync(downloadRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(PdfBytes, await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Different_user_cannot_discover_or_download_document_and_attempts_are_audited()
    {
        var owner = await RegisterAndGetSessionAsync();
        using var upload = await UploadPdfAsync(owner);
        Assert.Equal(System.Net.HttpStatusCode.Created, upload.StatusCode);
        var document = await upload.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(document);

        var otherUser = await RegisterAndGetSessionAsync();
        using var listRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            "/api/documents",
            otherUser);
        using var otherUsersList = await _client.SendAsync(listRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, otherUsersList.StatusCode);
        var otherUsersDocuments =
            await otherUsersList.Content.ReadFromJsonAsync<DocumentResponse[]>();
        Assert.NotNull(otherUsersDocuments);
        Assert.DoesNotContain(otherUsersDocuments, item => item.Id == document.Id);

        using var metadataRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}",
            otherUser);
        using var metadata = await _client.SendAsync(metadataRequest);
        using var downloadRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}/content",
            otherUser);
        using var download = await _client.SendAsync(downloadRequest);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, metadata.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, download.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var deniedEvents = await database.AuditEvents
            .AsNoTracking()
            .Where(item => item.DocumentId == document.Id
                && item.Result == TrustVault.Domain.Audit.AuditResult.Denied)
            .ToListAsync();
        Assert.Equal(2, deniedEvents.Count);
        Assert.All(deniedEvents, item => Assert.Equal(64, item.RecordHash.Length));
    }

    [Theory]
    [InlineData("READ")]
    [InlineData("EDIT")]
    public async Task Owner_can_grant_access_and_shared_user_can_read_until_revoked(
        string permission)
    {
        var owner = await RegisterAndGetSessionAsync();
        var recipient = await RegisterAndGetSessionAsync();
        using var upload = await UploadPdfAsync(owner);
        Assert.Equal(System.Net.HttpStatusCode.Created, upload.StatusCode);
        var document = await upload.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(document);

        using var grantRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"/api/documents/{document.Id}/grants",
            owner,
            JsonContent.Create(new
            {
                email = recipient.Email,
                permission
            }));
        using var granted = await _client.SendAsync(grantRequest);
        Assert.Equal(System.Net.HttpStatusCode.Created, granted.StatusCode);

        using var grantsRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}/grants",
            owner);
        using var grantsResponse = await _client.SendAsync(grantsRequest);
        Assert.True(
            grantsResponse.StatusCode == System.Net.HttpStatusCode.OK,
            await grantsResponse.Content.ReadAsStringAsync());
        var activeGrants =
            await grantsResponse.Content.ReadFromJsonAsync<DocumentGrantResponse[]>();
        var activeGrant = Assert.Single(activeGrants!);
        Assert.Equal(recipient.UserId, activeGrant.UserId.ToString());
        Assert.Equal(recipient.Email, activeGrant.Email);
        Assert.Equal(permission, activeGrant.Permission);

        using var listRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            "/api/documents",
            recipient);
        using var list = await _client.SendAsync(listRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, list.StatusCode);
        var sharedDocuments = await list.Content.ReadFromJsonAsync<DocumentResponse[]>();
        Assert.NotNull(sharedDocuments);
        var sharedDocument = Assert.Single(sharedDocuments);
        Assert.Equal(document.Id, sharedDocument.Id);
        Assert.False(sharedDocument.IsOwner);
        Assert.Equal(permission, sharedDocument.Permission);

        using var metadataRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}",
            recipient);
        using var metadata = await _client.SendAsync(metadataRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, metadata.StatusCode);

        using var downloadRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}/content",
            recipient);
        using var download = await _client.SendAsync(downloadRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(PdfBytes, await download.Content.ReadAsByteArrayAsync());

        using var revokeRequest = CreateAuthorizedRequest(
            HttpMethod.Delete,
            $"/api/documents/{document.Id}/grants/{Guid.Parse(recipient.UserId)}",
            owner);
        using var revoked = await _client.SendAsync(revokeRequest);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, revoked.StatusCode);

        using var listAfterRevokeRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            "/api/documents",
            recipient);
        using var listAfterRevoke = await _client.SendAsync(listAfterRevokeRequest);
        var remainingDocuments =
            await listAfterRevoke.Content.ReadFromJsonAsync<DocumentResponse[]>();
        Assert.NotNull(remainingDocuments);
        Assert.Empty(remainingDocuments);

        using var metadataAfterRevokeRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}",
            recipient);
        using var metadataAfterRevoke = await _client.SendAsync(metadataAfterRevokeRequest);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, metadataAfterRevoke.StatusCode);

        using var downloadAfterRevokeRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}/content",
            recipient);
        using var downloadAfterRevoke = await _client.SendAsync(downloadAfterRevokeRequest);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, downloadAfterRevoke.StatusCode);
    }

    [Fact]
    public async Task Non_owner_cannot_grant_or_revoke_document_access()
    {
        var owner = await RegisterAndGetSessionAsync();
        var otherUser = await RegisterAndGetSessionAsync();
        var recipient = await RegisterAndGetSessionAsync();
        using var upload = await UploadPdfAsync(owner);
        var document = await upload.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(document);

        using var grantRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"/api/documents/{document.Id}/grants",
            otherUser,
            JsonContent.Create(new { email = "unknown@example.test", permission = "READ" }));
        using var grant = await _client.SendAsync(grantRequest);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, grant.StatusCode);

        using var revokeRequest = CreateAuthorizedRequest(
            HttpMethod.Delete,
            $"/api/documents/{document.Id}/grants/{Guid.Parse(recipient.UserId)}",
            otherUser);
        using var revoke = await _client.SendAsync(revokeRequest);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, revoke.StatusCode);
    }

    [Fact]
    public async Task Owner_can_change_active_permission_without_mutating_grant_history()
    {
        var owner = await RegisterAndGetSessionAsync();
        var recipient = await RegisterAndGetSessionAsync();
        using var upload = await UploadPdfAsync(owner);
        var document = await upload.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(document);
        var email = recipient.Email;

        using var readGrantRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"/api/documents/{document.Id}/grants",
            owner,
            JsonContent.Create(new { email, permission = "READ" }));
        using var readGrant = await _client.SendAsync(readGrantRequest);
        Assert.Equal(System.Net.HttpStatusCode.Created, readGrant.StatusCode);

        using var editGrantRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"/api/documents/{document.Id}/grants",
            owner,
            JsonContent.Create(new { email, permission = "EDIT" }));
        using var editGrant = await _client.SendAsync(editGrantRequest);
        Assert.Equal(System.Net.HttpStatusCode.Created, editGrant.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var history = await database.DocumentGrants
            .AsNoTracking()
            .Where(grant => grant.DocumentId == document.Id
                && grant.UserId == Guid.Parse(recipient.UserId))
            .OrderBy(grant => grant.GrantedAtUtc)
            .ToListAsync();
        Assert.Equal(2, history.Count);
        Assert.NotNull(history[0].RevokedAtUtc);
        Assert.Null(history[1].RevokedAtUtc);
        Assert.Equal(TrustVault.Domain.Documents.DocumentPermission.Edit, history[1].Permission);
    }

    [Theory]
    [InlineData("report.txt", "%PDF-1.7\nsample")]
    [InlineData("report.pdf", "not a PDF")]
    public async Task Upload_rejects_files_with_an_invalid_extension_or_signature(
        string fileName,
        string contents)
    {
        var owner = await RegisterAndGetSessionAsync();
        using var response = await UploadFileAsync(
            owner.AccessToken,
            fileName,
            Encoding.ASCII.GetBytes(contents));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);

        using var listRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            "/api/documents",
            owner);
        using var list = await _client.SendAsync(listRequest);
        var documents = await list.Content.ReadFromJsonAsync<DocumentResponse[]>();
        Assert.NotNull(documents);
        Assert.Empty(documents);
    }

    [Fact]
    public async Task Upload_rejects_files_larger_than_the_limit()
    {
        var owner = await RegisterAndGetSessionAsync();
        var oversizedPdf = new byte[10 * 1024 * 1024 + 1];
        Encoding.ASCII.GetBytes("%PDF-1.7").CopyTo(oversizedPdf, 0);

        using var response = await UploadFileAsync(
            owner.AccessToken,
            "large.pdf",
            oversizedPdf);

        Assert.Contains(
            response.StatusCode,
            new[]
            {
                System.Net.HttpStatusCode.BadRequest,
                System.Net.HttpStatusCode.RequestEntityTooLarge
            });
    }

    private const string TestPassword = "ValidPassword123!";
    private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes(
        "%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF");

    private async Task<AuthResponse> RegisterAndGetSessionAsync()
    {
        using var response = await RegisterAsync(NewEmail());
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(auth);
        return auth;
    }

    private Task<HttpResponseMessage> UploadPdfAsync(AuthResponse auth) =>
        UploadFileAsync(auth.AccessToken, "report.pdf", PdfBytes);

    private Task<HttpResponseMessage> UploadPdfAsync(string accessToken) =>
        UploadFileAsync(accessToken, "report.pdf", PdfBytes);

    private async Task<HttpResponseMessage> UploadFileAsync(
        string accessToken,
        string fileName,
        byte[] fileContents)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("Project report"), "Title");
        content.Add(new StringContent("Sprint 2"), "Category");
        content.Add(new ByteArrayContent(fileContents), "File", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/documents")
        {
            Content = content
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        return await _client.SendAsync(request);
    }

    private static HttpRequestMessage CreateAuthorizedRequest(
        HttpMethod method,
        string path,
        AuthResponse auth,
        HttpContent? content = null)
    {
        var request = CreateAuthorizedRequest(method, path, auth.AccessToken);
        request.Content = content;
        return request;
    }

    private static HttpRequestMessage CreateAuthorizedRequest(
        HttpMethod method,
        string path,
        string accessToken,
        HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = content
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

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
    private readonly string _storageRoot;

    public ApiFactory()
    {
        _storageRoot = Path.Combine(
            Path.GetTempPath(),
            $"trustvault-tests-{Guid.NewGuid():N}");
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
        testSettings["RateLimits__AuthenticationPermitLimit"] = "100";
        testSettings["Storage__RootPath"] = _storageRoot;
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

        if (Directory.Exists(_storageRoot))
        {
            Directory.Delete(_storageRoot, recursive: true);
        }
    }
}
