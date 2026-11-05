using System.IO.Compression;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
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
using TrustVault.Api.Models.Admin;
using TrustVault.Api.Models.Documents;
using TrustVault.Api.Services;
using TrustVault.Application.Documents;
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
    public async Task Readiness_endpoint_is_anonymous_and_reports_database_state()
    {
        var response = await _client.GetAsync("/api/system/ready");

        Assert.Contains(
            response.StatusCode,
            new[]
            {
                System.Net.HttpStatusCode.OK,
                System.Net.HttpStatusCode.ServiceUnavailable
            });
    }

    [Fact]
    public async Task Readiness_endpoint_accepts_valid_token_and_reports_database_state()
    {
        var administrator = await GetAdministratorSessionAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/system/ready");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                administrator.AccessToken);

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
        var administrator = await GetAdministratorSessionAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/system/whoami");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                administrator.AccessToken);

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
    public async Task Administrator_creates_member_with_temporary_password_and_member_must_change_it()
    {
        var email = NewEmail();
        using var response = await AddMemberAsync(email);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        var member = await response.Content.ReadFromJsonAsync<MemberCreatedResponse>();
        Assert.NotNull(member);
        Assert.Equal(email, member.Email);
        Assert.Equal(UserRoles.Viewer, member.Role);
        Assert.True(member.PasswordChangeRequired);
        Assert.False(string.IsNullOrWhiteSpace(member.TemporaryPassword));

        using var loginResponse = await LoginAsync(email, member.TemporaryPassword);
        Assert.Equal(System.Net.HttpStatusCode.OK, loginResponse.StatusCode);
        var login = await loginResponse.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(login);
        Assert.True(login.PasswordChangeRequired);
        using var blockedRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            "/api/system/whoami",
            login.AccessToken);
        using var blockedResponse = await _client.SendAsync(blockedRequest);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, blockedResponse.StatusCode);

        using var changeRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            "/api/auth/change-password",
            login.AccessToken,
            JsonContent.Create(new
            {
                currentPassword = member.TemporaryPassword,
                newPassword = TestPassword
            }));
        using var rejectedChangeRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            "/api/auth/change-password",
            login.AccessToken,
            JsonContent.Create(new
            {
                currentPassword = member.TemporaryPassword,
                newPassword = "lowercasepassword123!"
            }));
        using var rejectedChangeResponse = await _client.SendAsync(rejectedChangeRequest);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, rejectedChangeResponse.StatusCode);

        using var changeResponse = await _client.SendAsync(changeRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, changeResponse.StatusCode);
        var changed = await changeResponse.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(changed);
        Assert.False(changed.PasswordChangeRequired);

        using var request = CreateAuthorizedRequest(
            HttpMethod.Get,
            "/api/system/whoami",
            changed.AccessToken);
        using var whoAmI = await _client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.OK, whoAmI.StatusCode);
    }

    [Fact]
    public async Task Public_registration_is_not_available()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/auth/register",
            new { email = NewEmail(), password = TestPassword });
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Member_creation_ignores_attempts_to_assign_a_role()
    {
        var email = NewEmail();
        using var response = await AddMemberAsync(email, UserRoles.Administrator);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<MemberCreatedResponse>();
        Assert.NotNull(created);
        Assert.Equal(UserRoles.Viewer, created.Role);

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var member = await userManager.FindByEmailAsync(email);
        Assert.NotNull(member);
        Assert.Equal(new[] { UserRoles.Viewer }, await userManager.GetRolesAsync(member));
    }

    [Fact]
    public async Task Only_administrator_can_create_members()
    {
        var member = await CreateMemberAndSetPasswordAsync(NewEmail());
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/members")
        {
            Content = JsonContent.Create(new
            {
                email = NewEmail(),
            })
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", member.AccessToken);
        using var response = await _client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Removing_member_immediately_revokes_issued_access_and_refresh_tokens()
    {
        var email = NewEmail();
        var memberSession = await CreateMemberAndSetPasswordAsync(email);
        using var memberListRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            "/api/admin/members",
            await GetAdministratorSessionAsync());
        using var memberListResponse = await _client.SendAsync(memberListRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, memberListResponse.StatusCode);
        var members = await memberListResponse.Content.ReadFromJsonAsync<MemberResponse[]>();
        Assert.NotNull(members);
        var member = Assert.Single(members, item => item.Email == email);

        using var removeRequest = CreateAuthorizedRequest(
            HttpMethod.Delete,
            $"/api/admin/members/{member.UserId}",
            await GetAdministratorSessionAsync());
        using var removeResponse = await _client.SendAsync(removeRequest);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, removeResponse.StatusCode);

        using var whoAmIRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            "/api/system/whoami",
            memberSession.AccessToken);
        using var whoAmIResponse = await _client.SendAsync(whoAmIRequest);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, whoAmIResponse.StatusCode);

        using var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh")
        {
            Content = JsonContent.Create(new { refreshToken = memberSession.RefreshToken })
        };
        using var refreshResponse = await _client.SendAsync(refreshRequest);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, refreshResponse.StatusCode);
    }

    [Fact]
    public async Task Removing_document_owner_preserves_documents_and_audit_but_revokes_grantees()
    {
        var owner = await CreateMemberAndSetPasswordAsync(NewEmail());
        var reader = await CreateMemberAndSetPasswordAsync(NewEmail());
        using var upload = await UploadPdfAsync(owner);
        Assert.Equal(System.Net.HttpStatusCode.Created, upload.StatusCode);
        var document = await upload.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(document);

        using var grantRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"/api/documents/{document.Id}/grants",
            owner,
            JsonContent.Create(new { email = reader.Email, permission = "READ" }));
        using var grantResponse = await _client.SendAsync(grantRequest);
        Assert.Equal(System.Net.HttpStatusCode.Created, grantResponse.StatusCode);

        var administrator = await GetAdministratorSessionAsync();
        using var listRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            "/api/admin/members",
            administrator);
        using var listResponse = await _client.SendAsync(listRequest);
        var members = await listResponse.Content.ReadFromJsonAsync<MemberResponse[]>();
        Assert.NotNull(members);
        var ownerMember = Assert.Single(members, item => item.Email == owner.Email);
        using var removeRequest = CreateAuthorizedRequest(
            HttpMethod.Delete,
            $"/api/admin/members/{ownerMember.UserId}",
            administrator);
        using var removeResponse = await _client.SendAsync(removeRequest);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, removeResponse.StatusCode);

        using var readerRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}",
            reader);
        using var readerResponse = await _client.SendAsync(readerRequest);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, readerResponse.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True(await database.Documents.AnyAsync(item => item.Id == document.Id));
        Assert.True(await database.AuditEvents.AnyAsync(item =>
            item.DocumentId == document.Id && item.Action == "document.upload"));
        Assert.True(await database.DocumentGrants.AnyAsync(item =>
            item.DocumentId == document.Id && item.UserId == Guid.Parse(reader.UserId)
                && item.RevokedAtUtc != null));
    }

    [Fact]
    public async Task Administrator_cannot_remove_the_primary_administrator()
    {
        var administrator = await GetAdministratorSessionAsync();
        using var request = CreateAuthorizedRequest(
            HttpMethod.Delete,
            $"/api/admin/members/{administrator.UserId}",
            administrator);
        using var response = await _client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Bootstrap_creates_exactly_one_primary_administrator()
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var administrators = await userManager.GetUsersInRoleAsync(UserRoles.Administrator);

        var administrator = Assert.Single(administrators);
        Assert.True(administrator.IsPrimaryAdministrator);
        Assert.Equal(ApiFactory.AdministratorEmail, administrator.Email);
    }

    [Fact]
    public async Task Logout_invalidates_the_current_access_and_refresh_tokens()
    {
        var member = await CreateMemberAndSetPasswordAsync(NewEmail());
        using var logoutRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            "/api/auth/logout",
            member);
        using var logoutResponse = await _client.SendAsync(logoutRequest);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, logoutResponse.StatusCode);

        using var whoAmIRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            "/api/system/whoami",
            member.AccessToken);
        using var whoAmIResponse = await _client.SendAsync(whoAmIRequest);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, whoAmIResponse.StatusCode);

        using var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh")
        {
            Content = JsonContent.Create(new { refreshToken = member.RefreshToken })
        };
        using var refreshResponse = await _client.SendAsync(refreshRequest);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, refreshResponse.StatusCode);
    }

    [Fact]
    public async Task Administrator_member_creation_rejects_duplicate_email()
    {
        var email = NewEmail();
        using var created = await AddMemberAsync(email);
        using var duplicate = await AddMemberAsync(email);

        Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Administrator_can_reactivate_removed_member_with_same_email()
    {
        var email = NewEmail();
        _ = await CreateMemberAndSetPasswordAsync(email);
        var administrator = await GetAdministratorSessionAsync();

        using var memberListRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            "/api/admin/members",
            administrator);
        using var memberListResponse = await _client.SendAsync(memberListRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, memberListResponse.StatusCode);
        var members = await memberListResponse.Content.ReadFromJsonAsync<MemberResponse[]>();
        Assert.NotNull(members);
        var originalMember = Assert.Single(members, item => item.Email == email);

        using var removeRequest = CreateAuthorizedRequest(
            HttpMethod.Delete,
            $"/api/admin/members/{originalMember.UserId}",
            administrator);
        using var removeResponse = await _client.SendAsync(removeRequest);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, removeResponse.StatusCode);

        using var reactivationResponse = await AddMemberAsync(email);
        Assert.Equal(System.Net.HttpStatusCode.Created, reactivationResponse.StatusCode);
        var reactivated = await reactivationResponse.Content.ReadFromJsonAsync<MemberCreatedResponse>();
        Assert.NotNull(reactivated);
        Assert.Equal(originalMember.UserId, reactivated.UserId);
        Assert.Equal(UserRoles.Viewer, reactivated.Role);
        Assert.True(reactivated.PasswordChangeRequired);

        using var oldPasswordLogin = await LoginAsync(email, TestPassword);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, oldPasswordLogin.StatusCode);
        using var temporaryPasswordLogin = await LoginAsync(email, reactivated.TemporaryPassword);
        Assert.Equal(System.Net.HttpStatusCode.OK, temporaryPasswordLogin.StatusCode);
        var session = await temporaryPasswordLogin.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(session);
        Assert.True(session.PasswordChangeRequired);
    }

    [Fact]
    public async Task Login_returns_token_for_valid_credentials()
    {
        var email = NewEmail();
        _ = await CreateMemberAndSetPasswordAsync(email);
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
        _ = await CreateMemberAndSetPasswordAsync(email);
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
    public async Task Refresh_token_endpoint_rotates_tokens_and_rejects_stale_token()
    {
        var email = NewEmail();
        _ = await CreateMemberAndSetPasswordAsync(email);
        using var login = await LoginAsync(email, TestPassword);
        var initialAuth = await login.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(initialAuth);
        Assert.False(string.IsNullOrWhiteSpace(initialAuth.RefreshToken));

        using var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh")
        {
            Content = JsonContent.Create(new { refreshToken = initialAuth.RefreshToken })
        };
        using var refreshResponse = await _client.SendAsync(refreshRequest);

        Assert.Equal(System.Net.HttpStatusCode.OK, refreshResponse.StatusCode);
        var rotatedAuth = await refreshResponse.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(rotatedAuth);
        Assert.False(string.IsNullOrWhiteSpace(rotatedAuth.RefreshToken));
        Assert.NotEqual(initialAuth.RefreshToken, rotatedAuth.RefreshToken);
        Assert.NotNull(rotatedAuth.RefreshTokenExpiresAtUtc);

        using var staleRefreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh")
        {
            Content = JsonContent.Create(new { refreshToken = initialAuth.RefreshToken })
        };
        using var staleRefreshResponse = await _client.SendAsync(staleRefreshRequest);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, staleRefreshResponse.StatusCode);
    }

    [Fact]
    public async Task Login_locks_account_after_repeated_failed_passwords()
    {
        var email = NewEmail();
        _ = await CreateMemberAndSetPasswordAsync(email);
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
        _ = await CreateMemberAndSetPasswordAsync(email);
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
    public async Task Administrator_oversight_endpoints_are_forbidden_to_viewers()
    {
        var viewer = await RegisterAndGetSessionAsync();
        using var dashboardRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            "/api/admin/dashboard",
            viewer);
        using var auditRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            "/api/admin/audit",
            viewer);
        using var documentsRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            "/api/admin/documents",
            viewer);
        using var dashboard = await _client.SendAsync(dashboardRequest);
        using var audit = await _client.SendAsync(auditRequest);
        using var documents = await _client.SendAsync(documentsRequest);

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, dashboard.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, audit.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, documents.StatusCode);
    }

    [Fact]
    public async Task Administrator_pagination_rejects_offsets_that_overflow()
    {
        var administrator = await GetAdministratorSessionAsync();
        using var auditRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            "/api/admin/audit?page=2147483647&pageSize=100",
            administrator);
        using var documentsRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            "/api/admin/documents?page=2147483647&pageSize=100",
            administrator);
        using var sharesRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            "/api/admin/shares?page=2147483647&pageSize=100",
            administrator);
        using var audit = await _client.SendAsync(auditRequest);
        using var documents = await _client.SendAsync(documentsRequest);
        using var shares = await _client.SendAsync(sharesRequest);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, audit.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, documents.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, shares.StatusCode);
    }

    [Fact]
    public async Task Administrator_can_search_export_and_verify_audit_events()
    {
        var member = await CreateMemberAndSetPasswordAsync(NewEmail());
        using var upload = await UploadPdfAsync(member);
        Assert.Equal(System.Net.HttpStatusCode.Created, upload.StatusCode);
        var document = await upload.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(document);
        var administrator = await GetAdministratorSessionAsync();

        using var searchRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/admin/audit?user={Uri.EscapeDataString(member.Email)}"
                + $"&documentId={document.Id}&action=document.upload&outcome=DENIED",
            administrator);
        using var searchResponse = await _client.SendAsync(searchRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, searchResponse.StatusCode);
        var search = await searchResponse.Content.ReadFromJsonAsync<AdminAuditPageResponse>();
        Assert.NotNull(search);
        Assert.Equal(0, search.TotalCount);

        using var exportRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/admin/audit/export?user={Uri.EscapeDataString(member.Email)}"
                + $"&documentId={document.Id}&action=document.upload",
            administrator);
        using var export = await _client.SendAsync(exportRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, export.StatusCode);
        Assert.Contains("actor_email", await export.Content.ReadAsStringAsync());
        Assert.Contains(member.Email, await export.Content.ReadAsStringAsync());

        using var verifyRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            "/api/admin/audit/verify",
            administrator);
        using var verify = await _client.SendAsync(verifyRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, verify.StatusCode);
    }

    [Fact]
    public async Task Administrator_can_review_metadata_and_revoke_permission_without_content_access()
    {
        var owner = await CreateMemberAndSetPasswordAsync(NewEmail());
        var recipient = await CreateMemberAndSetPasswordAsync(NewEmail());
        using var upload = await UploadPdfAsync(owner);
        Assert.Equal(System.Net.HttpStatusCode.Created, upload.StatusCode);
        var document = await upload.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(document);

        using var grantRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"/api/documents/{document.Id}/grants",
            owner,
            JsonContent.Create(new { email = recipient.Email, permission = "READ" }));
        using var grant = await _client.SendAsync(grantRequest);
        Assert.Equal(System.Net.HttpStatusCode.Created, grant.StatusCode);

        var administrator = await GetAdministratorSessionAsync();
        using var metadataRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/admin/documents?search={Uri.EscapeDataString(document.Title)}",
            administrator);
        using var metadataResponse = await _client.SendAsync(metadataRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, metadataResponse.StatusCode);
        var metadataJson = await metadataResponse.Content.ReadAsStringAsync();
        Assert.Contains(document.Id.ToString(), metadataJson);
        Assert.DoesNotContain("storageKey", metadataJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contentBytes", metadataJson, StringComparison.OrdinalIgnoreCase);

        using var permissionsRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/admin/documents/{document.Id}/permissions",
            administrator);
        using var permissionsResponse = await _client.SendAsync(permissionsRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, permissionsResponse.StatusCode);
        var permissions = await permissionsResponse.Content
            .ReadFromJsonAsync<AdminPermissionResponse[]>();
        Assert.NotNull(permissions);
        Assert.Contains(permissions, item => item.Email == recipient.Email);

        using var revokeRequest = CreateAuthorizedRequest(
            HttpMethod.Delete,
            $"/api/admin/documents/{document.Id}/permissions/{recipient.UserId}",
            administrator,
            JsonContent.Create(new { reason = "Incident response review" }));
        using var revoked = await _client.SendAsync(revokeRequest);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, revoked.StatusCode);

        using var recipientDownloadRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}/content",
            recipient);
        using var recipientDownload = await _client.SendAsync(recipientDownloadRequest);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, recipientDownload.StatusCode);

        using var administratorDownloadRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}/content",
            administrator);
        using var administratorDownload = await _client.SendAsync(administratorDownloadRequest);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, administratorDownload.StatusCode);
    }

    [Fact]
    public async Task Administrator_can_review_named_shares_and_reassign_an_orphaned_document()
    {
        var oldOwner = await CreateMemberAndSetPasswordAsync(NewEmail());
        var newOwner = await CreateMemberAndSetPasswordAsync(NewEmail());
        var reader = await CreateMemberAndSetPasswordAsync(NewEmail());
        using var upload = await UploadPdfAsync(oldOwner);
        Assert.Equal(System.Net.HttpStatusCode.Created, upload.StatusCode);
        var document = await upload.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(document);

        using var grantRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"/api/documents/{document.Id}/grants",
            oldOwner,
            JsonContent.Create(new { email = reader.Email, permission = "READ" }));
        using var grant = await _client.SendAsync(grantRequest);
        Assert.Equal(System.Net.HttpStatusCode.Created, grant.StatusCode);

        var administrator = await GetAdministratorSessionAsync();
        using var sharesRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/admin/shares?search={Uri.EscapeDataString(reader.Email)}",
            administrator);
        using var sharesResponse = await _client.SendAsync(sharesRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, sharesResponse.StatusCode);
        var shares = await sharesResponse.Content.ReadFromJsonAsync<AdminSharePageResponse>();
        Assert.NotNull(shares);
        Assert.Contains(shares.Items, item =>
            item.DocumentId == document.Id && item.UserEmail == reader.Email);

        using var reassignRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"/api/admin/documents/{document.Id}/reassign-owner",
            administrator,
            JsonContent.Create(new
            {
                newOwnerId = newOwner.UserId,
                reason = "Original owner account was removed"
            }));
        using var reassign = await _client.SendAsync(reassignRequest);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, reassign.StatusCode);

        using var oldOwnerRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}/content",
            oldOwner);
        using var oldOwnerDownload = await _client.SendAsync(oldOwnerRequest);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, oldOwnerDownload.StatusCode);

        using var newOwnerRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}/content",
            newOwner);
        using var newOwnerDownload = await _client.SendAsync(newOwnerRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, newOwnerDownload.StatusCode);
        Assert.Equal(PdfBytes, await newOwnerDownload.Content.ReadAsByteArrayAsync());

        using var readerRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}/content",
            reader);
        using var readerDownload = await _client.SendAsync(readerRequest);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, readerDownload.StatusCode);
    }

    [Fact]
    public async Task Administrator_dashboard_alert_and_settings_endpoints_return_non_content_data()
    {
        var administrator = await GetAdministratorSessionAsync();
        using var dashboardRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            "/api/admin/dashboard",
            administrator);
        using var dashboard = await _client.SendAsync(dashboardRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, dashboard.StatusCode);
        var dashboardJson = await dashboard.Content.ReadAsStringAsync();
        Assert.Contains("activeDocuments", dashboardJson);
        Assert.DoesNotContain("storageKey", dashboardJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contentBytes", dashboardJson, StringComparison.OrdinalIgnoreCase);

        using var alertsRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            "/api/admin/security-alerts",
            administrator);
        using var alerts = await _client.SendAsync(alertsRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, alerts.StatusCode);

        using var settingsRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            "/api/admin/settings",
            administrator);
        using var settings = await _client.SendAsync(settingsRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, settings.StatusCode);
        var settingsJson = await settings.Content.ReadAsStringAsync();
        Assert.Contains("externalGuestLinksEnabled", settingsJson);
        Assert.DoesNotContain("PasswordHash", settingsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storagePath", settingsJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Administrator_credential_reset_invalidates_old_tokens_and_requires_password_change()
    {
        var email = NewEmail();
        var member = await CreateMemberAndSetPasswordAsync(email);
        var administrator = await GetAdministratorSessionAsync();

        using var resetRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"/api/admin/members/{member.UserId}/reset-credentials",
            administrator,
            JsonContent.Create(new { reason = "Member reported compromised password" }));
        using var resetResponse = await _client.SendAsync(resetRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, resetResponse.StatusCode);
        var reset = await resetResponse.Content.ReadFromJsonAsync<AdminResetCredentialsResponse>();
        Assert.NotNull(reset);
        Assert.True(reset.PasswordChangeRequired);
        Assert.False(string.IsNullOrWhiteSpace(reset.TemporaryPassword));

        using var oldTokenRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            "/api/system/whoami",
            member);
        using var oldTokenResponse = await _client.SendAsync(oldTokenRequest);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, oldTokenResponse.StatusCode);

        using var loginResponse = await LoginAsync(email, reset.TemporaryPassword);
        Assert.Equal(System.Net.HttpStatusCode.OK, loginResponse.StatusCode);
        var login = await loginResponse.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(login);
        Assert.True(login.PasswordChangeRequired);
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
    public async Task Upload_rejection_and_scanner_failure_are_not_available_for_download_or_editing()
    {
        var owner = await RegisterAndGetSessionAsync();

        using var rejectedUpload = await UploadFileAsync(
            owner.AccessToken,
            "rejected.pdf",
            Encoding.ASCII.GetBytes("%PDF-1.7\nREJECT_TEST"));
        Assert.Equal(System.Net.HttpStatusCode.Created, rejectedUpload.StatusCode);
        var rejected = await rejectedUpload.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(rejected);
        Assert.Equal("REJECTED", rejected.ScanStatus);

        using var rejectedDownloadRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{rejected.Id}/content",
            owner);
        using var rejectedDownload = await _client.SendAsync(rejectedDownloadRequest);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, rejectedDownload.StatusCode);

        using var rejectedEditRequest = CreateAuthorizedRequest(
            HttpMethod.Put,
            $"/api/documents/{rejected.Id}",
            owner,
            JsonContent.Create(new
            {
                title = "Must stay blocked",
                description = (string?)null,
                category = "Test"
            }));
        using var rejectedEdit = await _client.SendAsync(rejectedEditRequest);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, rejectedEdit.StatusCode);

        using var failedUpload = await UploadFileAsync(
            owner.AccessToken,
            "scanner-failed.pdf",
            Encoding.ASCII.GetBytes("%PDF-1.7\nSCAN_FAIL_ALWAYS"));
        Assert.Equal(System.Net.HttpStatusCode.Created, failedUpload.StatusCode);
        var failed = await failedUpload.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(failed);
        Assert.Equal("SCAN_FAILED", failed.ScanStatus);

        using var failedDownloadRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{failed.Id}/content",
            owner);
        using var failedDownload = await _client.SendAsync(failedDownloadRequest);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, failedDownload.StatusCode);

        using var retryRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"/api/documents/{failed.Id}/scan/retry",
            owner);
        using var retry = await _client.SendAsync(retryRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, retry.StatusCode);
        var retryResult = await retry.Content.ReadFromJsonAsync<DocumentScanStatusResponse>();
        Assert.NotNull(retryResult);
        Assert.Equal("SCAN_FAILED", retryResult.ScanStatus);
        Assert.Equal(2, retryResult.ScanAttemptCount);

        using var retryAfterFailureRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"/api/documents/{rejected.Id}/scan/retry",
            owner);
        using var retryAfterFailure = await _client.SendAsync(retryAfterFailureRequest);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, retryAfterFailure.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var uploadAudit = await database.AuditEvents
            .AsNoTracking()
            .Where(item => item.DocumentId == rejected.Id
                && item.Action == "document.upload")
            .SingleAsync();
        using var auditMetadata = JsonDocument.Parse(uploadAudit.MetadataJson);
        Assert.Equal(
            "REJECTED",
            auditMetadata.RootElement.GetProperty("scanStatus").GetString());
        Assert.Equal(TrustVault.Domain.Audit.AuditResult.Denied, uploadAudit.Result);
    }

    [Fact]
    public async Task Failed_replacement_is_not_activated_and_prior_clean_version_remains_available()
    {
        var owner = await RegisterAndGetSessionAsync();
        using var upload = await UploadPdfAsync(owner);
        var document = await upload.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(document);

        using var replacementRequest = CreateAuthorizedRequest(
            HttpMethod.Put,
            $"/api/documents/{document.Id}/content",
            owner,
            CreatePdfContent(
                "failed-replacement.pdf",
                Encoding.ASCII.GetBytes("%PDF-1.7\nSCAN_FAIL_ALWAYS")));
        using var replacement = await _client.SendAsync(replacementRequest);
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, replacement.StatusCode);

        using var downloadRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}/content",
            owner);
        using var download = await _client.SendAsync(downloadRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(PdfBytes, await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Rejected_text_edit_does_not_replace_the_clean_document()
    {
        var owner = await RegisterAndGetSessionAsync();
        using var upload = await UploadFileAsync(
            owner.AccessToken,
            "notes.txt",
            Encoding.UTF8.GetBytes("original text"));
        var document = await upload.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(document);

        using var editRequest = CreateAuthorizedRequest(
            HttpMethod.Put,
            $"/api/documents/{document.Id}/editable-text",
            owner,
            JsonContent.Create(new { text = "REJECT_TEST" }));
        using var edit = await _client.SendAsync(editRequest);
        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, edit.StatusCode);

        using var downloadRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}/content",
            owner);
        using var download = await _client.SendAsync(downloadRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(
            "original text",
            Encoding.UTF8.GetString(await download.Content.ReadAsByteArrayAsync()));
    }

    [Fact]
    public async Task Failed_scan_can_be_retried_to_clean_and_downloaded()
    {
        var owner = await RegisterAndGetSessionAsync();
        using var upload = await UploadFileAsync(
            owner.AccessToken,
            "retry.pdf",
            Encoding.ASCII.GetBytes("%PDF-1.7\nRETRY_TO_CLEAN"));
        var document = await upload.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(document);
        Assert.Equal("SCAN_FAILED", document.ScanStatus);

        using var retryRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"/api/documents/{document.Id}/scan/retry",
            owner);
        using var retry = await _client.SendAsync(retryRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, retry.StatusCode);
        var result = await retry.Content.ReadFromJsonAsync<DocumentScanStatusResponse>();
        Assert.NotNull(result);
        Assert.Equal("CLEAN", result.ScanStatus);
        Assert.Equal(2, result.ScanAttemptCount);

        using var downloadRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}/content",
            owner);
        using var download = await _client.SendAsync(downloadRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, download.StatusCode);
    }

    [Fact]
    public async Task Pending_document_and_version_are_blocked_until_owner_retries_scan()
    {
        var owner = await RegisterAndGetSessionAsync();
        using var upload = await UploadPdfAsync(owner);
        var document = await upload.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(document);

        using var replacementRequest = CreateAuthorizedRequest(
            HttpMethod.Put,
            $"/api/documents/{document.Id}/content",
            owner,
            CreatePdfContent(
                "replacement.pdf",
                Encoding.ASCII.GetBytes("%PDF-1.7\nreplacement")));
        using var replacement = await _client.SendAsync(replacementRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, replacement.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var storedDocument = await database.Documents.SingleAsync(item => item.Id == document.Id);
            storedDocument.ScanStatus = TrustVault.Domain.Documents.DocumentScanStatus.PendingScan;
            var storedVersion = await database.DocumentVersions
                .SingleAsync(item => item.DocumentId == document.Id);
            storedVersion.ScanStatus = TrustVault.Domain.Documents.DocumentScanStatus.PendingScan;
            await database.SaveChangesAsync();
        }

        using var blockedDownloadRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}/content",
            owner);
        using var blockedDownload = await _client.SendAsync(blockedDownloadRequest);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, blockedDownload.StatusCode);

        using var blockedEditRequest = CreateAuthorizedRequest(
            HttpMethod.Put,
            $"/api/documents/{document.Id}",
            owner,
            JsonContent.Create(new
            {
                title = "Must stay blocked",
                description = (string?)null,
                category = "Test"
            }));
        using var blockedEdit = await _client.SendAsync(blockedEditRequest);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, blockedEdit.StatusCode);

        var otherUser = await RegisterAndGetSessionAsync();
        using var unauthorizedRetryRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"/api/documents/{document.Id}/scan/retry",
            otherUser);
        using var unauthorizedRetry = await _client.SendAsync(unauthorizedRetryRequest);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, unauthorizedRetry.StatusCode);

        using var retryDocumentRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"/api/documents/{document.Id}/scan/retry",
            owner);
        using var retryDocument = await _client.SendAsync(retryDocumentRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, retryDocument.StatusCode);

        using var retryVersionRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"/api/documents/{document.Id}/versions/1/scan/retry",
            owner);
        using var retryVersion = await _client.SendAsync(retryVersionRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, retryVersion.StatusCode);

        using var versionDownloadRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}/versions/1/content",
            owner);
        using var versionDownload = await _client.SendAsync(versionDownloadRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, versionDownload.StatusCode);
        Assert.Equal(PdfBytes, await versionDownload.Content.ReadAsByteArrayAsync());
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
    public async Task Document_audits_show_create_read_edit_and_require_active_document_access()
    {
        var owner = await RegisterAndGetSessionAsync();
        var reader = await RegisterAndGetSessionAsync();
        using var upload = await UploadPdfAsync(owner);
        Assert.Equal(System.Net.HttpStatusCode.Created, upload.StatusCode);
        var document = await upload.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(document);

        using var downloadRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}/content",
            owner);
        using var download = await _client.SendAsync(downloadRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, download.StatusCode);

        using var editRequest = CreateAuthorizedRequest(
            HttpMethod.Put,
            $"/api/documents/{document.Id}",
            owner,
            JsonContent.Create(new
            {
                title = "Updated title",
                description = (string?)null,
                category = "Reviewed"
            }));
        using var edit = await _client.SendAsync(editRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, edit.StatusCode);

        using var grantRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"/api/documents/{document.Id}/grants",
            owner,
            JsonContent.Create(new { email = reader.Email, permission = "READ" }));
        using var grant = await _client.SendAsync(grantRequest);
        Assert.Equal(System.Net.HttpStatusCode.Created, grant.StatusCode);

        using var readerDownloadRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}/content",
            reader);
        using var readerDownload = await _client.SendAsync(readerDownloadRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, readerDownload.StatusCode);

        using var deniedEditRequest = CreateAuthorizedRequest(
            HttpMethod.Put,
            $"/api/documents/{document.Id}",
            reader,
            JsonContent.Create(new
            {
                title = "Unauthorized title",
                description = (string?)null,
                category = "Reviewed"
            }));
        using var deniedEdit = await _client.SendAsync(deniedEditRequest);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, deniedEdit.StatusCode);

        using var auditsRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}/audits",
            reader);
        using var auditsResponse = await _client.SendAsync(auditsRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, auditsResponse.StatusCode);
        var audits = await auditsResponse.Content.ReadFromJsonAsync<DocumentAuditResponse[]>();
        Assert.NotNull(audits);
        Assert.Contains(audits, item =>
            item.Activity == "CREATED"
            && item.ActorEmail == owner.Email
            && item.Result == "SUCCESS");
        Assert.Contains(audits, item =>
            item.Activity == "READ"
            && item.ActorEmail == reader.Email
            && item.Result == "SUCCESS");
        Assert.Contains(audits, item =>
            item.Activity == "EDITED"
            && item.ActorEmail == owner.Email
            && item.Result == "SUCCESS");
        Assert.Contains(audits, item =>
            item.Activity == "EDITED"
            && item.ActorEmail == reader.Email
            && item.Result == "DENIED");
        Assert.Equal(
            audits.OrderByDescending(item => item.TimestampUtc).Select(item => item.Id),
            audits.Select(item => item.Id));

        var stranger = await RegisterAndGetSessionAsync();
        using var deniedAuditsRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}/audits",
            stranger);
        using var deniedAudits = await _client.SendAsync(deniedAuditsRequest);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, deniedAudits.StatusCode);

        using var revokeRequest = CreateAuthorizedRequest(
            HttpMethod.Delete,
            $"/api/documents/{document.Id}/grants/{reader.UserId}",
            owner);
        using var revoked = await _client.SendAsync(revokeRequest);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, revoked.StatusCode);

        using var revokedAuditsRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}/audits",
            reader);
        using var revokedAudits = await _client.SendAsync(revokedAuditsRequest);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, revokedAudits.StatusCode);
    }

    [Fact]
    public async Task Owner_removing_document_revokes_all_grants_and_removes_file_access()
    {
        var owner = await RegisterAndGetSessionAsync();
        var reader = await RegisterAndGetSessionAsync();
        var editor = await RegisterAndGetSessionAsync();
        using var upload = await UploadPdfAsync(owner);
        Assert.Equal(System.Net.HttpStatusCode.Created, upload.StatusCode);
        var document = await upload.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(document);

        using var replaceRequest = CreateAuthorizedRequest(
            HttpMethod.Put,
            $"/api/documents/{document.Id}/content",
            owner,
            CreatePdfContent("replacement.pdf", Encoding.ASCII.GetBytes("%PDF-1.7\nreplacement")));
        using var replaced = await _client.SendAsync(replaceRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, replaced.StatusCode);

        string[] storageKeys;
        using (var keyScope = _factory.Services.CreateScope())
        {
            var keyDatabase = keyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var currentStorageKey = await keyDatabase.Documents
                .AsNoTracking()
                .Where(item => item.Id == document.Id)
                .Select(item => item.StorageKey)
                .SingleAsync();
            var previousStorageKeys = await keyDatabase.DocumentVersions
                .AsNoTracking()
                .Where(version => version.DocumentId == document.Id)
                .Select(version => version.StorageKey)
                .ToArrayAsync();
            storageKeys = previousStorageKeys.Append(currentStorageKey).ToArray();
        }

        foreach (var (recipient, permission) in new[]
        {
            (reader, "READ"),
            (editor, "EDIT")
        })
        {
            using var grantRequest = CreateAuthorizedRequest(
                HttpMethod.Post,
                $"/api/documents/{document.Id}/grants",
                owner,
                JsonContent.Create(new { email = recipient.Email, permission }));
            using var grant = await _client.SendAsync(grantRequest);
            Assert.Equal(System.Net.HttpStatusCode.Created, grant.StatusCode);
        }

        using var unauthorizedDeleteRequest = CreateAuthorizedRequest(
            HttpMethod.Delete,
            $"/api/documents/{document.Id}",
            reader);
        using var unauthorizedDelete = await _client.SendAsync(unauthorizedDeleteRequest);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, unauthorizedDelete.StatusCode);

        using var deleteRequest = CreateAuthorizedRequest(
            HttpMethod.Delete,
            $"/api/documents/{document.Id}",
            owner);
        using var deleted = await _client.SendAsync(deleteRequest);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, deleted.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var storedDocument = await database.Documents
            .AsNoTracking()
            .SingleAsync(item => item.Id == document.Id);
        Assert.True(storedDocument.IsDeleted);
        var grants = await database.DocumentGrants
            .AsNoTracking()
            .Where(grant => grant.DocumentId == document.Id)
            .ToListAsync();
        Assert.Equal(2, grants.Count);
        Assert.All(grants, grant =>
        {
            Assert.NotNull(grant.RevokedAtUtc);
            Assert.Equal("Document removed by owner", grant.RevocationReason);
        });
        Assert.Empty(await database.DocumentVersions
            .AsNoTracking()
            .Where(version => version.DocumentId == document.Id)
            .ToListAsync());
        var storageRoot = Environment.GetEnvironmentVariable("Storage__RootPath");
        Assert.False(string.IsNullOrWhiteSpace(storageRoot));
        Assert.All(
            storageKeys,
            storageKey => Assert.False(File.Exists(Path.Combine(storageRoot!, $"{storageKey}.pdf"))));
        Assert.Contains(
            await database.AuditEvents.AsNoTracking().Where(
                auditEvent => auditEvent.DocumentId == document.Id).ToListAsync(),
            auditEvent => auditEvent.Action == "document.delete"
                && auditEvent.Result == TrustVault.Domain.Audit.AuditResult.Success);

        foreach (var token in new[] { reader.AccessToken, editor.AccessToken })
        {
            using var downloadRequest = CreateAuthorizedRequest(
                HttpMethod.Get,
                $"/api/documents/{document.Id}/content",
                token);
            using var download = await _client.SendAsync(downloadRequest);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, download.StatusCode);
        }

        using var ownerDownloadRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"/api/documents/{document.Id}/content",
            owner);
        using var ownerDownload = await _client.SendAsync(ownerDownloadRequest);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, ownerDownload.StatusCode);
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
        return await CreateMemberAndSetPasswordAsync(NewEmail());
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

    private async Task<HttpResponseMessage> AddMemberAsync(string email, string? attemptedRole = null)
    {
        var administrator = await GetAdministratorSessionAsync();
        var content = attemptedRole is null
            ? JsonContent.Create(new { email })
            : JsonContent.Create(new { email, role = attemptedRole });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/members")
        {
            Content = content
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                administrator.AccessToken);
        return await _client.SendAsync(request);
    }

    private async Task<AuthResponse> CreateMemberAndSetPasswordAsync(string email)
    {
        using var createResponse = await AddMemberAsync(email);
        Assert.Equal(System.Net.HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<MemberCreatedResponse>();
        Assert.NotNull(created);

        using var loginResponse = await LoginAsync(email, created.TemporaryPassword);
        Assert.Equal(System.Net.HttpStatusCode.OK, loginResponse.StatusCode);
        var login = await loginResponse.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(login);
        Assert.True(login.PasswordChangeRequired);

        using var changeRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            "/api/auth/change-password",
            login.AccessToken,
            JsonContent.Create(new
            {
                currentPassword = created.TemporaryPassword,
                newPassword = TestPassword
            }));
        using var changeResponse = await _client.SendAsync(changeRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, changeResponse.StatusCode);
        var changed = await changeResponse.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(changed);
        return changed;
    }

    private async Task<AuthResponse> GetAdministratorSessionAsync()
    {
        using var response = await LoginAsync(
            ApiFactory.AdministratorEmail,
            ApiFactory.AdministratorPassword);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var session = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(session);
        Assert.Contains(UserRoles.Administrator, session.Roles);
        return session;
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
    public const string AdministratorEmail = "administrator@example.test";
    public const string AdministratorPassword = "AdminTestPassword123!";
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
            ["Jwt__Audience"] = "TrustVault.Tests",
            ["AdminBootstrap__Email"] = AdministratorEmail,
            ["AdminBootstrap__Password"] = AdministratorPassword
        };
        testSettings["ConnectionStrings__TrustVault"] =
            "Host=localhost;Port=55432;Database=trustvault;Username=trustvault_app;Password=local_only";
        testSettings["Jwt__AccessTokenLifetimeMinutes"] = "15";
        testSettings["RateLimits__AuthenticationPermitLimit"] = "1000";
        testSettings["Storage__RootPath"] = _storageRoot;
        testSettings["ConnectionStrings__TrustVault"] =
            "Host=localhost;Port=55432;Database=trustvault;Username=trustvault_runtime;Password=runtime_local";
        var testConnectionString = Environment.GetEnvironmentVariable("TRUSTVAULT_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(testConnectionString))
        {
            testSettings["ConnectionStrings__TrustVault"] = testConnectionString;
        }

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
            services.RemoveAll<IPrivateDocumentScanner>();
            services.AddSingleton<IPrivateDocumentScanner, TestDocumentScanner>();
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

    public sealed class TestDocumentScanner : IPrivateDocumentScanner
    {
        private readonly ConcurrentDictionary<string, int> _retryAttempts = new();

        public async Task<DocumentScanVerdict> ScanAsync(
            Stream content,
            CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            var text = Encoding.ASCII.GetString(buffer.ToArray());
            if (text.Contains("REJECT_TEST", StringComparison.Ordinal))
            {
                return DocumentScanVerdict.Rejected;
            }

            if (text.Contains("SCAN_FAIL_ALWAYS", StringComparison.Ordinal))
            {
                throw new IOException("Test scanner is unavailable.");
            }

            if (text.Contains("RETRY_TO_CLEAN", StringComparison.Ordinal)
                && _retryAttempts.AddOrUpdate(text, 1, (_, attempts) => attempts + 1) == 1)
            {
                throw new IOException("Test scanner is temporarily unavailable.");
            }

            return DocumentScanVerdict.Clean;
        }
    }
}
