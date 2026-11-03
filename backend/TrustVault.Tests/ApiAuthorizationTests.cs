using System.IO.Compression;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using TrustVault.Api.Models.Auth;
using TrustVault.Api.Models.Documents;
using TrustVault.Api.Services;
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

    [Fact]
    public async Task Owner_can_grant_read_access_and_shared_user_can_read_until_revoked()
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
                permission = "READ"
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
        Assert.Equal("READ", activeGrant.Permission);

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
        Assert.Equal("READ", sharedDocument.Permission);

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
    public async Task Owner_can_upgrade_read_grant_to_edit_without_losing_grant_history()
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
        Assert.Equal(TrustVault.Domain.Documents.DocumentPermission.Read, history[0].Permission);
        Assert.Null(history[1].RevokedAtUtc);
        Assert.Equal(TrustVault.Domain.Documents.DocumentPermission.Edit, history[1].Permission);
    }

    [Fact]
    public async Task Edit_grantee_can_update_metadata_and_replace_content_but_read_grantee_cannot()
    {
        var owner = await RegisterAndGetSessionAsync();
        var editor = await RegisterAndGetSessionAsync();
        var reader = await RegisterAndGetSessionAsync();
        using var upload = await UploadPdfAsync(owner);
        var document = await upload.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(document);

        using var editorGrantRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"/api/documents/{document.Id}/grants",
            owner,
            JsonContent.Create(new { email = editor.Email, permission = "EDIT" }));
        using var editorGrant = await _client.SendAsync(editorGrantRequest);
        Assert.Equal(System.Net.HttpStatusCode.Created, editorGrant.StatusCode);

        using var readerGrantRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"/api/documents/{document.Id}/grants",
            owner,
            JsonContent.Create(new { email = reader.Email, permission = "READ" }));
        using var readerGrant = await _client.SendAsync(readerGrantRequest);
        Assert.Equal(System.Net.HttpStatusCode.Created, readerGrant.StatusCode);

        using var readerMetadataRequest = CreateAuthorizedRequest(
            HttpMethod.Put,
            $"/api/documents/{document.Id}",
            reader,
            JsonContent.Create(new
            {
                title = "Unauthorized title",
                description = (string?)null,
                category = "Test"
            }));
        using var readerMetadata = await _client.SendAsync(readerMetadataRequest);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, readerMetadata.StatusCode);

        using var readerReplacementRequest = CreateAuthorizedRequest(
            HttpMethod.Put,
            $"/api/documents/{document.Id}/content",
            reader,
            CreatePdfContent("replacement.pdf", Encoding.ASCII.GetBytes("%PDF-1.7\nblocked")));
        using var readerReplacement = await _client.SendAsync(readerReplacementRequest);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, readerReplacement.StatusCode);

        using var editorMetadataRequest = CreateAuthorizedRequest(
            HttpMethod.Put,
            $"/api/documents/{document.Id}",
            editor,
            JsonContent.Create(new
            {
                title = "Updated title",
                description = "Updated by editor",
                category = "Reviewed"
            }));
        using var editorMetadata = await _client.SendAsync(editorMetadataRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, editorMetadata.StatusCode);
        var updatedDocument = await editorMetadata.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(updatedDocument);
        Assert.Equal("Updated title", updatedDocument.Title);
        Assert.Equal("Updated by editor", updatedDocument.Description);
        Assert.Equal("Reviewed", updatedDocument.Category);
        Assert.Equal("EDIT", updatedDocument.Permission);

        var replacementBytes = Encoding.ASCII.GetBytes("%PDF-1.7\nreplacement contents");
        using var editorReplacementRequest = CreateAuthorizedRequest(
            HttpMethod.Put,
            $"/api/documents/{document.Id}/content",
            editor,
            CreatePdfContent("replacement.pdf", replacementBytes));
        using var editorReplacement = await _client.SendAsync(editorReplacementRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, editorReplacement.StatusCode);

        using var editorDownloadRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}/content",
            editor);
        using var editorDownload = await _client.SendAsync(editorDownloadRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, editorDownload.StatusCode);
        Assert.Equal(replacementBytes, await editorDownload.Content.ReadAsByteArrayAsync());

        using var versionsRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}/versions",
            reader);
        using var versionsResponse = await _client.SendAsync(versionsRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, versionsResponse.StatusCode);
        var versions = await versionsResponse.Content.ReadFromJsonAsync<DocumentVersionResponse[]>();
        Assert.NotNull(versions);
        var previousVersion = Assert.Single(versions);
        Assert.Equal(1, previousVersion.VersionNumber);

        using var previousVersionRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}/versions/1/content",
            reader);
        using var previousVersionResponse = await _client.SendAsync(previousVersionRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, previousVersionResponse.StatusCode);
        Assert.Equal(PdfBytes, await previousVersionResponse.Content.ReadAsByteArrayAsync());

        var redactedBytes = Encoding.ASCII.GetBytes("%PDF-1.7\nredacted");
        var redactedContent = CreatePdfContent("redacted.pdf", redactedBytes, true);
        using var redactionRequest = CreateAuthorizedRequest(
            HttpMethod.Put,
            $"/api/documents/{document.Id}/content",
            editor,
            redactedContent);
        using var redactionResponse = await _client.SendAsync(redactionRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, redactionResponse.StatusCode);

        using var versionsAfterRedactionRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}/versions",
            reader);
        using var versionsAfterRedaction = await _client.SendAsync(versionsAfterRedactionRequest);
        var remainingVersions =
            await versionsAfterRedaction.Content.ReadFromJsonAsync<DocumentVersionResponse[]>();
        Assert.NotNull(remainingVersions);
        Assert.Empty(remainingVersions);

        using var oldVersionAfterRedactionRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}/versions/1/content",
            reader);
        using var oldVersionAfterRedaction =
            await _client.SendAsync(oldVersionAfterRedactionRequest);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, oldVersionAfterRedaction.StatusCode);
    }

    [Fact]
    public async Task Existing_edit_grant_remains_read_only_and_is_listed_as_edit()
    {
        var owner = await RegisterAndGetSessionAsync();
        var recipient = await RegisterAndGetSessionAsync();
        using var upload = await UploadPdfAsync(owner);
        var document = await upload.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(document);

        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.DocumentGrants.Add(new TrustVault.Domain.Documents.DocumentGrant
            {
                Id = Guid.NewGuid(),
                DocumentId = document.Id,
                UserId = Guid.Parse(recipient.UserId),
                Permission = TrustVault.Domain.Documents.DocumentPermission.Edit,
                GrantedByUserId = Guid.Parse(owner.UserId),
                GrantedAtUtc = DateTime.UtcNow
            });
            await database.SaveChangesAsync();
        }

        using var listRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            "/api/documents",
            recipient);
        using var list = await _client.SendAsync(listRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, list.StatusCode);
        var sharedDocuments = await list.Content.ReadFromJsonAsync<DocumentResponse[]>();
        Assert.NotNull(sharedDocuments);
        var sharedDocument = Assert.Single(sharedDocuments);
        Assert.Equal("EDIT", sharedDocument.Permission);

        using var downloadRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}/content",
            recipient);
        using var download = await _client.SendAsync(downloadRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(PdfBytes, await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Upload_accepts_supported_document_types_and_preserves_the_content_type()
    {
        var owner = await RegisterAndGetSessionAsync();
        var files = new[]
        {
            (FileName: "notes.txt", Bytes: Encoding.UTF8.GetBytes("Plain text content")),
            (FileName: "legacy.doc", Bytes: new byte[]
            {
                0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00
            }),
            (FileName: "report.docx", Bytes: CreateDocxBytesForTest())
        };

        foreach (var (fileName, bytes) in files)
        {
            using var upload = await UploadFileAsync(owner.AccessToken, fileName, bytes);
            Assert.Equal(System.Net.HttpStatusCode.Created, upload.StatusCode);
            var document = await upload.Content.ReadFromJsonAsync<DocumentResponse>();
            Assert.NotNull(document);

            using var downloadRequest = CreateAuthorizedRequest(
                HttpMethod.Get,
                $"/api/documents/{document.Id}/content",
                owner);
            using var download = await _client.SendAsync(downloadRequest);
            Assert.Equal(System.Net.HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
            Assert.Equal(fileName, download.Content.Headers.ContentDisposition?.FileNameStar
                ?? download.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

            var expectedContentType = Path.GetExtension(fileName).ToLowerInvariant() switch
            {
                ".txt" => "text/plain",
                ".doc" => "application/msword",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                _ => throw new InvalidOperationException("Unexpected test document extension.")
            };
            Assert.Equal(expectedContentType, download.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task Owner_can_edit_text_and_word_documents_and_previous_versions_are_preserved()
    {
        var owner = await RegisterAndGetSessionAsync();
        var originalText = Encoding.UTF8.GetBytes("First line\nSecond line");
        using var textUpload = await UploadFileAsync(owner.AccessToken, "notes.txt", originalText);
        var textDocument = await textUpload.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(textDocument);

        using var textReadRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{textDocument.Id}/editable-text",
            owner);
        using var textRead = await _client.SendAsync(textReadRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, textRead.StatusCode);
        var editableText = await textRead.Content.ReadFromJsonAsync<DocumentEditableTextResponse>();
        Assert.NotNull(editableText);
        Assert.Equal("First line\nSecond line", editableText.Text);

        using var textUpdateRequest = CreateAuthorizedRequest(
            HttpMethod.Put,
            $"/api/documents/{textDocument.Id}/editable-text",
            owner,
            JsonContent.Create(new { text = "Updated text" }));
        using var textUpdate = await _client.SendAsync(textUpdateRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, textUpdate.StatusCode);

        using var textDownloadRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{textDocument.Id}/content",
            owner);
        using var textDownload = await _client.SendAsync(textDownloadRequest);
        Assert.Equal("Updated text", await textDownload.Content.ReadAsStringAsync());

        using var textVersionRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{textDocument.Id}/versions/1/content",
            owner);
        using var textVersion = await _client.SendAsync(textVersionRequest);
        Assert.Equal(originalText, await textVersion.Content.ReadAsByteArrayAsync());

        var docxBytes = CreateDocxBytesForTest("Original Word text");
        using var wordUpload = await UploadFileAsync(owner.AccessToken, "report.docx", docxBytes);
        var wordDocument = await wordUpload.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(wordDocument);

        var reader = await RegisterAndGetSessionAsync();
        using var wordReaderGrantRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"/api/documents/{wordDocument.Id}/grants",
            owner,
            JsonContent.Create(new { email = reader.Email, permission = "READ" }));
        using var wordReaderGrant = await _client.SendAsync(wordReaderGrantRequest);
        Assert.Equal(System.Net.HttpStatusCode.Created, wordReaderGrant.StatusCode);

        using var deniedTextReadRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{wordDocument.Id}/editable-text",
            reader);
        using var deniedTextRead = await _client.SendAsync(deniedTextReadRequest);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, deniedTextRead.StatusCode);

        using var deniedTextUpdateRequest = CreateAuthorizedRequest(
            HttpMethod.Put,
            $"/api/documents/{wordDocument.Id}/editable-text",
            reader,
            JsonContent.Create(new { text = "Should not be saved" }));
        using var deniedTextUpdate = await _client.SendAsync(deniedTextUpdateRequest);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, deniedTextUpdate.StatusCode);

        using var wordReadRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{wordDocument.Id}/editable-text",
            owner);
        using var wordRead = await _client.SendAsync(wordReadRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, wordRead.StatusCode);
        var wordText = await wordRead.Content.ReadFromJsonAsync<DocumentEditableTextResponse>();
        Assert.NotNull(wordText);
        Assert.Contains("Original Word text", wordText.Text);

        using var wordUpdateRequest = CreateAuthorizedRequest(
            HttpMethod.Put,
            $"/api/documents/{wordDocument.Id}/editable-text",
            owner,
            JsonContent.Create(new { text = "Edited Word text" }));
        using var wordUpdate = await _client.SendAsync(wordUpdateRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, wordUpdate.StatusCode);
        var updatedWord = await wordUpdate.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(updatedWord);
        Assert.Equal("report.docx", updatedWord.OriginalFileName);

        using var wordDownloadRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{wordDocument.Id}/content",
            owner);
        using var wordDownload = await _client.SendAsync(wordDownloadRequest);
        var savedDocxBytes = await wordDownload.Content.ReadAsByteArrayAsync();
        using var savedDocxStream = new MemoryStream(savedDocxBytes);
        Assert.Equal(
            "Edited Word text",
            await new ApiFactory.TestDocumentTextConverter().ExtractTextAsync(
                savedDocxStream,
                ".docx",
                CancellationToken.None));

        using var wordVersionRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{wordDocument.Id}/versions/1/content",
            owner);
        using var wordVersion = await _client.SendAsync(wordVersionRequest);
        var originalDocx = await wordVersion.Content.ReadAsByteArrayAsync();
        using var originalDocxStream = new MemoryStream(originalDocx);
        Assert.Contains(
            "Original Word text",
            await new ApiFactory.TestDocumentTextConverter().ExtractTextAsync(
                originalDocxStream,
                ".docx",
                CancellationToken.None));

        var legacyWordBytes = new byte[]
        {
            0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00
        };
        using var legacyUpload = await UploadFileAsync(
            owner.AccessToken,
            "legacy-report.doc",
            legacyWordBytes);
        var legacyDocument = await legacyUpload.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(legacyDocument);

        using var legacyUpdateRequest = CreateAuthorizedRequest(
            HttpMethod.Put,
            $"/api/documents/{legacyDocument.Id}/editable-text",
            owner,
            JsonContent.Create(new { text = "Edited legacy Word text" }));
        using var legacyUpdate = await _client.SendAsync(legacyUpdateRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, legacyUpdate.StatusCode);
        var convertedLegacyDocument =
            await legacyUpdate.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(convertedLegacyDocument);
        Assert.Equal("legacy-report.docx", convertedLegacyDocument.OriginalFileName);

        using var legacyVersionRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{legacyDocument.Id}/versions/1/content",
            owner);
        using var legacyVersion = await _client.SendAsync(legacyVersionRequest);
        Assert.Equal(legacyWordBytes, await legacyVersion.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("report.exe", "%PDF-1.7\nsample")]
    [InlineData("report.pdf", "not a PDF")]
    [InlineData("report.docx", "not a Word document")]
    [InlineData("report.doc", "not a Word document")]
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

    internal static byte[] CreateDocxBytesForTest(string text = "")
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var contentTypes = new StreamWriter(
                archive.CreateEntry("[Content_Types].xml").Open()))
            {
                contentTypes.Write(
                    "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
            }

            using (var relationships = new StreamWriter(
                archive.CreateEntry("_rels/.rels").Open()))
            {
                relationships.Write(
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            }

            using (var document = new StreamWriter(
                archive.CreateEntry("word/document.xml").Open()))
            {
                var paragraphs = string.Join(
                    string.Empty,
                    text.Split('\n').Select(line =>
                        $"<w:p><w:r><w:t>{SecurityElement.Escape(line)}</w:t></w:r></w:p>"));
                document.Write(
                    $"<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>{paragraphs}<w:sectPr/></w:body></w:document>");
            }
        }

        return stream.ToArray();
    }

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

    private static MultipartFormDataContent CreatePdfContent(
        string fileName,
        byte[] bytes,
        bool purgePreviousVersions = false)
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(bytes), "file", fileName);
        content.Add(
            new StringContent(purgePreviousVersions.ToString()),
            "purgePreviousVersions");
        return content;
    }

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

    protected override void ConfigureWebHost(
        Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDocumentTextConverter>();
            services.AddSingleton<IDocumentTextConverter, ApiFactory.TestDocumentTextConverter>();
        });
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

    public sealed class TestDocumentTextConverter : IDocumentTextConverter
    {
        public async Task<string> ExtractTextAsync(
            Stream content,
            string extension,
            CancellationToken cancellationToken)
        {
            if (extension.Equals(".doc", StringComparison.OrdinalIgnoreCase))
            {
                return "Original Word text";
            }

            using var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
            var documentEntry = archive.GetEntry("word/document.xml")
                ?? throw new InvalidDataException("Test DOCX has no document part.");
            await using var documentStream = documentEntry.Open();
            var document = await XDocument.LoadAsync(
                documentStream,
                LoadOptions.None,
                cancellationToken);
            XNamespace wordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            return string.Join(
                Environment.NewLine,
                document.Descendants(wordNamespace + "p").Select(paragraph =>
                    string.Concat(paragraph.Descendants(wordNamespace + "t").Select(node => node.Value))));
        }

        public Task<byte[]> CreateDocxAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(ApiAuthorizationTests.CreateDocxBytesForTest(text));
    }
}
