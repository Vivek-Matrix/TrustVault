using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TrustVault.Api.Models.Admin;
using TrustVault.Api.Services;
using TrustVault.Domain.Audit;
using TrustVault.Domain.Documents;
using TrustVault.Domain.Security;
using TrustVault.Infrastructure.Persistence;
using TrustVault.Infrastructure.Persistence.Entities;

namespace TrustVault.Api.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Roles = UserRoles.Administrator)]
public sealed class AdminOversightController(
    ApplicationDbContext database,
    UserManager<ApplicationUser> userManager,
    DocumentAuditWriter auditWriter,
    IConfiguration configuration) : ControllerBase
{
    private const int MaximumPageSize = 100;
    private const int MaximumExportRows = 5000;
    private const int DeniedAttemptThreshold = 5;
    private const int BulkDownloadThreshold = 20;

    [HttpGet("dashboard")]
    [ProducesResponseType<AdminDashboardResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AdminDashboardResponse>> GetDashboard(
        CancellationToken cancellationToken)
    {
        var since = DateTime.UtcNow.AddHours(-24);
        var result = new AdminDashboardResponse(
            await database.Users.CountAsync(
                user => !user.IsSuspended && !user.IsPrimaryAdministrator,
                cancellationToken),
            await database.Users.CountAsync(
                user => user.IsSuspended && !user.IsPrimaryAdministrator,
                cancellationToken),
            await database.Documents.CountAsync(
                document => !document.IsDeleted,
                cancellationToken),
            await database.Documents.CountAsync(
                document => !document.IsDeleted
                    && (document.ScanStatus == DocumentScanStatus.PendingScan
                        || document.ScanStatus == DocumentScanStatus.Scanning),
                cancellationToken),
            await database.DocumentGrants.CountAsync(
                grant => grant.RevokedAtUtc == null,
                cancellationToken),
            await database.AuditEvents.CountAsync(
                item => item.TimestampUtc >= since && item.Result == AuditResult.Denied,
                cancellationToken),
            await database.AuditEvents.CountAsync(
                item => item.TimestampUtc >= since
                    && item.Action == "document.download"
                    && item.Result == AuditResult.Success,
                cancellationToken));

        return Ok(result);
    }

    [HttpGet("audit")]
    [ProducesResponseType<AdminAuditPageResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AdminAuditPageResponse>> SearchAudit(
        [FromQuery] string? user,
        [FromQuery] Guid? documentId,
        [FromQuery] string? action,
        [FromQuery] DateTimeOffset? fromUtc,
        [FromQuery] DateTimeOffset? toUtc,
        [FromQuery] string? ipAddress,
        [FromQuery] string? outcome,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateAuditFilters(
            fromUtc,
            toUtc,
            page,
            pageSize,
            outcome,
            out var resultName))
        {
            return ValidationProblem(ModelState);
        }

        var query = BuildAuditQuery(
            user,
            documentId,
            action,
            fromUtc,
            toUtc,
            ipAddress,
            resultName);
        var totalCount = await query.CountAsync(cancellationToken);
        var events = await query
            .OrderByDescending(item => item.TimestampUtc)
            .ThenByDescending(item => item.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        var response = await MapAuditEventsAsync(events, cancellationToken);
        return Ok(new AdminAuditPageResponse(page, pageSize, totalCount, response));
    }

    [HttpGet("audit/export")]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportAudit(
        [FromQuery] string? user,
        [FromQuery] Guid? documentId,
        [FromQuery] string? action,
        [FromQuery] DateTimeOffset? fromUtc,
        [FromQuery] DateTimeOffset? toUtc,
        [FromQuery] string? ipAddress,
        [FromQuery] string? outcome,
        CancellationToken cancellationToken)
    {
        if (!TryValidateAuditFilters(
            fromUtc,
            toUtc,
            1,
            1,
            outcome,
            out var resultName))
        {
            return ValidationProblem(ModelState);
        }

        var query = BuildAuditQuery(
            user,
            documentId,
            action,
            fromUtc,
            toUtc,
            ipAddress,
            resultName);
        var totalCount = await query.CountAsync(cancellationToken);
        if (totalCount > MaximumExportRows)
        {
            return Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "The audit export is too large.",
                detail: $"Narrow the filters to {MaximumExportRows} events or fewer.");
        }

        var events = await query
            .OrderBy(item => item.TimestampUtc)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var mapped = await MapAuditEventsAsync(events, cancellationToken);
        var csv = new StringBuilder(
            "id,actor_user_id,actor_email,actor_role,document_id,action,result,timestamp_utc,request_id,ip_address,user_agent\r\n");
        foreach (var item in mapped)
        {
            csv.AppendLine(string.Join(',', new[]
            {
                item.Id.ToString(),
                item.ActorUserId?.ToString(),
                item.ActorEmail,
                item.ActorRole,
                item.DocumentId?.ToString(),
                item.Action,
                item.Result,
                item.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
                item.RequestId,
                item.IpAddress,
                item.UserAgent
            }.Select(CsvEscape)));
        }

        return File(
            Encoding.UTF8.GetBytes(csv.ToString()),
            "text/csv; charset=utf-8",
            $"trustvault-audit-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    [HttpGet("audit/verify")]
    [ProducesResponseType<AuditChainVerificationResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AuditChainVerificationResult>> VerifyAuditChain(
        CancellationToken cancellationToken)
    {
        var events = await database.AuditEvents
            .AsNoTracking()
            .OrderBy(item => item.TimestampUtc)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return Ok(AuditChainVerifier.Verify(events));
    }

    [HttpGet("security-alerts")]
    [ProducesResponseType<AdminSecurityAlertResponse[]>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AdminSecurityAlertResponse>>> GetSecurityAlerts(
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var deniedStart = now.AddMinutes(-10);
        var downloadStart = now.AddHours(-1);
        var deniedEvents = await database.AuditEvents
            .AsNoTracking()
            .Where(item => item.TimestampUtc >= deniedStart
                && item.Result == AuditResult.Denied
                && item.ActorUserId != null)
            .GroupBy(item => new { item.ActorUserId, item.IpAddress })
            .Select(group => new
            {
                group.Key.ActorUserId,
                group.Key.IpAddress,
                Count = group.Count()
            })
            .Where(item => item.Count >= DeniedAttemptThreshold)
            .ToListAsync(cancellationToken);

        var downloadEvents = await database.AuditEvents
            .AsNoTracking()
            .Where(item => item.TimestampUtc >= downloadStart
                && item.Action == "document.download"
                && item.Result == AuditResult.Success
                && item.ActorUserId != null)
            .GroupBy(item => new { item.ActorUserId, item.IpAddress })
            .Select(group => new
            {
                group.Key.ActorUserId,
                group.Key.IpAddress,
                Count = group.Count()
            })
            .Where(item => item.Count >= BulkDownloadThreshold)
            .ToListAsync(cancellationToken);

        var actorIds = deniedEvents.Select(item => item.ActorUserId!.Value)
            .Concat(downloadEvents.Select(item => item.ActorUserId!.Value))
            .Distinct()
            .ToArray();
        var emails = await database.Users
            .AsNoTracking()
            .Where(user => actorIds.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => user.Email, cancellationToken);

        var alerts = deniedEvents.Select(item => new AdminSecurityAlertResponse(
                "Repeated denied requests",
                "HIGH",
                item.ActorUserId,
                emails.GetValueOrDefault(item.ActorUserId!.Value),
                item.IpAddress,
                item.Count,
                new DateTimeOffset(deniedStart),
                new DateTimeOffset(now)))
            .Concat(downloadEvents.Select(item => new AdminSecurityAlertResponse(
                "Unusual download volume",
                "MEDIUM",
                item.ActorUserId,
                emails.GetValueOrDefault(item.ActorUserId!.Value),
                item.IpAddress,
                item.Count,
                new DateTimeOffset(downloadStart),
                new DateTimeOffset(now))))
            .OrderBy(item => item.Severity == "HIGH" ? 0 : 1)
            .ThenByDescending(item => item.EventCount)
            .ToArray();

        return Ok(alerts);
    }

    [HttpGet("documents")]
    [ProducesResponseType<AdminDocumentPageResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AdminDocumentPageResponse>> SearchDocuments(
        [FromQuery] string? search,
        [FromQuery] string? owner,
        [FromQuery] string? category,
        [FromQuery] bool includeDeleted,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidPage(page, pageSize))
        {
            ModelState.AddModelError(
                "pageSize",
                $"Page must be positive and page size must be between 1 and {MaximumPageSize}.");
            return ValidationProblem(ModelState);
        }

        var query = database.Documents
            .AsNoTracking()
            .Join(
                database.Users.AsNoTracking(),
                document => document.OwnerId,
                user => user.Id,
                (document, user) => new { Document = document, User = user });
        if (!includeDeleted)
        {
            query = query.Where(item => !item.Document.IsDeleted);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var value = search.Trim();
            query = query.Where(item => item.Document.Title.Contains(value)
                || item.Document.OriginalFileName.Contains(value)
                || item.Document.Category.Contains(value));
        }

        if (!string.IsNullOrWhiteSpace(owner))
        {
            var value = owner.Trim();
            query = query.Where(item => item.User.Email!.Contains(value));
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            var value = category.Trim();
            query = query.Where(item => item.Document.Category.Contains(value));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(item => item.Document.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(item => new
            {
                item.Document,
                OwnerEmail = item.User.Email ?? string.Empty,
                VersionCount = item.Document.Versions.Count,
                GrantCount = item.Document.Grants.Count(grant => grant.RevokedAtUtc == null)
            })
            .ToListAsync(cancellationToken);

        var items = rows.Select(item => new AdminDocumentResponse(
                item.Document.Id,
                item.Document.Title,
                item.Document.Category,
                item.Document.OriginalFileName,
                item.Document.SizeBytes,
                item.Document.OwnerId,
                item.OwnerEmail,
                new DateTimeOffset(DateTime.SpecifyKind(
                    item.Document.CreatedAtUtc,
                    DateTimeKind.Utc)),
                new DateTimeOffset(DateTime.SpecifyKind(
                    item.Document.UpdatedAtUtc,
                    DateTimeKind.Utc)),
                item.VersionCount,
                item.GrantCount,
                item.Document.ScanStatus.ToString().ToUpperInvariant(),
                item.Document.IsDeleted))
            .ToArray();
        return Ok(new AdminDocumentPageResponse(page, pageSize, totalCount, items));
    }

    [HttpGet("shares")]
    [ProducesResponseType<AdminSharePageResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AdminSharePageResponse>> SearchShares(
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidPage(page, pageSize))
        {
            ModelState.AddModelError(
                "pageSize",
                $"Page must be positive and page size must be between 1 and {MaximumPageSize}.");
            return ValidationProblem(ModelState);
        }

        var query = database.DocumentGrants
            .AsNoTracking()
            .Where(grant => grant.RevokedAtUtc == null)
            .Join(
                database.Documents.AsNoTracking(),
                grant => grant.DocumentId,
                document => document.Id,
                (grant, document) => new { Grant = grant, Document = document })
            .Join(
                database.Users.AsNoTracking(),
                item => item.Grant.UserId,
                user => user.Id,
                (item, user) => new { item.Grant, item.Document, User = user })
            .Join(
                database.Users.AsNoTracking(),
                item => item.Document.OwnerId,
                owner => owner.Id,
                (item, owner) => new
                {
                    item.Grant,
                    item.Document,
                    item.User,
                    Owner = owner
                });
        if (!string.IsNullOrWhiteSpace(search))
        {
            var value = search.Trim();
            query = query.Where(item =>
                item.Document.Title.Contains(value)
                || (item.User.Email != null && item.User.Email.Contains(value))
                || (item.Owner.Email != null && item.Owner.Email.Contains(value)));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var shares = await query
            .OrderByDescending(item => item.Grant.GrantedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(item => new AdminShareResponse(
                item.Document.Id,
                item.Document.Title,
                item.Document.OwnerId,
                item.Owner.Email ?? string.Empty,
                item.User.Id,
                item.User.Email ?? string.Empty,
                item.Grant.Permission.ToString().ToUpperInvariant(),
                new DateTimeOffset(DateTime.SpecifyKind(
                    item.Grant.GrantedAtUtc,
                    DateTimeKind.Utc))))
            .ToArrayAsync(cancellationToken);

        return Ok(new AdminSharePageResponse(page, pageSize, totalCount, shares));
    }

    [HttpGet("documents/{documentId:guid}/permissions")]
    [ProducesResponseType<AdminPermissionResponse[]>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<AdminPermissionResponse>>> GetPermissionHistory(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        if (!await database.Documents.AnyAsync(
            document => document.Id == documentId,
            cancellationToken))
        {
            return NotFound();
        }

        var rows = await database.DocumentGrants
            .AsNoTracking()
            .Where(grant => grant.DocumentId == documentId)
            .Join(
                database.Users.AsNoTracking(),
                grant => grant.UserId,
                user => user.Id,
                (grant, user) => new { Grant = grant, UserEmail = user.Email ?? string.Empty })
            .OrderByDescending(item => item.Grant.GrantedAtUtc)
            .Select(item => new
            {
                item.Grant,
                item.UserEmail
            })
            .ToListAsync(cancellationToken);
        return Ok(rows.Select(item => new AdminPermissionResponse(
                item.Grant.UserId,
                item.UserEmail,
                item.Grant.Permission.ToString().ToUpperInvariant(),
                item.Grant.GrantedByUserId,
                new DateTimeOffset(DateTime.SpecifyKind(item.Grant.GrantedAtUtc, DateTimeKind.Utc)),
                item.Grant.RevokedAtUtc is DateTime revokedAt
                    ? new DateTimeOffset(DateTime.SpecifyKind(revokedAt, DateTimeKind.Utc))
                    : null,
                item.Grant.RevocationReason))
            .ToArray());
    }

    [HttpDelete("documents/{documentId:guid}/permissions/{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokePermission(
        Guid documentId,
        Guid userId,
        AdminRevokePermissionRequest request,
        CancellationToken cancellationToken)
    {
        var reason = request.Reason.Trim();
        if (reason.Length < 8)
        {
            ModelState.AddModelError(nameof(request.Reason), "Provide a reason of at least 8 characters.");
            return ValidationProblem(ModelState);
        }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var grant = await database.DocumentGrants.FirstOrDefaultAsync(
            item => item.DocumentId == documentId
                && item.UserId == userId
                && item.RevokedAtUtc == null,
            cancellationToken);
        if (grant is null)
        {
            return NotFound();
        }

        var email = await database.Users
            .Where(user => user.Id == userId)
            .Select(user => user.Email)
            .SingleOrDefaultAsync(cancellationToken);
        grant.RevokedAtUtc = DateTime.UtcNow;
        grant.RevocationReason = $"Administrator: {reason}";
        await auditWriter.WriteAsync(
            HttpContext,
            documentId,
            "admin.permission.revoke",
            AuditResult.Success,
            JsonSerializer.Serialize(new
            {
                userId,
                email,
                reason
            }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("documents/{documentId:guid}/reassign-owner")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReassignDocumentOwner(
        Guid documentId,
        AdminReassignDocumentRequest request,
        CancellationToken cancellationToken)
    {
        var reason = request.Reason.Trim();
        if (request.NewOwnerId == Guid.Empty || reason.Length < 8)
        {
            ModelState.AddModelError(
                nameof(request),
                "Select an active member as the new owner and provide a reason of at least 8 characters.");
            return ValidationProblem(ModelState);
        }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var document = await database.Documents.FirstOrDefaultAsync(
            item => item.Id == documentId && !item.IsDeleted,
            cancellationToken);
        var newOwner = await database.Users.FirstOrDefaultAsync(
            user => user.Id == request.NewOwnerId
                && !user.IsSuspended
                && !user.IsPrimaryAdministrator,
            cancellationToken);
        if (document is null || newOwner is null)
        {
            return NotFound();
        }

        if (document.OwnerId == newOwner.Id)
        {
            ModelState.AddModelError(
                nameof(request.NewOwnerId),
                "The selected member already owns this document.");
            return ValidationProblem(ModelState);
        }

        var previousOwnerId = document.OwnerId;
        var activeGrants = await database.DocumentGrants
            .Where(grant => grant.DocumentId == documentId && grant.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);
        var now = DateTime.UtcNow;
        foreach (var grant in activeGrants)
        {
            grant.RevokedAtUtc = now;
            grant.RevocationReason = "Revoked during administrator ownership reassignment";
        }

        document.OwnerId = newOwner.Id;
        document.UpdatedAtUtc = now;
        await auditWriter.WriteAsync(
            HttpContext,
            documentId,
            "admin.document.owner.reassign",
            AuditResult.Success,
            JsonSerializer.Serialize(new
            {
                previousOwnerId,
                newOwnerId = newOwner.Id,
                reason,
                revokedGrantCount = activeGrants.Count
            }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("members/{userId:guid}/reset-credentials")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [ProducesResponseType<AdminResetCredentialsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminResetCredentialsResponse>> ResetCredentials(
        Guid userId,
        AdminResetCredentialsRequest request,
        CancellationToken cancellationToken)
    {
        var reason = request.Reason.Trim();
        if (reason.Length < 8)
        {
            ModelState.AddModelError(nameof(request.Reason), "Provide a reason of at least 8 characters.");
            return ValidationProblem(ModelState);
        }

        var member = await userManager.FindByIdAsync(userId.ToString());
        if (member is null || member.IsPrimaryAdministrator || member.IsSuspended)
        {
            return NotFound();
        }

        var temporaryPassword = TemporaryPasswordGenerator.Generate();
        var resetToken = await userManager.GeneratePasswordResetTokenAsync(member);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var resetResult = await userManager.ResetPasswordAsync(member, resetToken, temporaryPassword);
        if (!resetResult.Succeeded)
        {
            return BadRequest(new ValidationProblemDetails(
                new Dictionary<string, string[]>
                {
                    ["password"] = resetResult.Errors.Select(error => error.Description).ToArray()
                }));
        }

        member.MustChangePassword = true;
        member.RefreshTokenHash = null;
        member.RefreshTokenExpiresAtUtc = null;
        member.RefreshTokenIssuedAtUtc = null;
        member.SecurityStamp = Guid.NewGuid().ToString();
        var updateResult = await userManager.UpdateAsync(member);
        if (!updateResult.Succeeded)
        {
            throw new InvalidOperationException(
                "Unable to update member credentials: "
                + string.Join("; ", updateResult.Errors.Select(error => error.Code)));
        }

        await auditWriter.WriteAsync(
            HttpContext,
            null,
            "member.credentials.reset",
            AuditResult.Success,
            JsonSerializer.Serialize(new
            {
                userId = member.Id,
                email = member.Email,
                reason
            }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Ok(new AdminResetCredentialsResponse(
            member.Id,
            member.Email ?? string.Empty,
            temporaryPassword,
            true));
    }

    [HttpGet("settings")]
    [ProducesResponseType<AdminSystemSettingsResponse>(StatusCodes.Status200OK)]
    public ActionResult<AdminSystemSettingsResponse> GetSettings()
    {
        var tokenLifetime = configuration.GetValue("Jwt:AccessTokenLifetimeMinutes", 15);
        return Ok(new AdminSystemSettingsResponse(
            [".pdf", ".txt", ".doc", ".docx"],
            10 * 1024 * 1024,
            tokenLifetime,
            5,
            15,
            false,
            "Read-only; change through reviewed deployment configuration"));
    }

    private IQueryable<AuditEvent> BuildAuditQuery(
        string? user,
        Guid? documentId,
        string? action,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        string? ipAddress,
        string? resultName)
    {
        IQueryable<AuditEvent> query = database.AuditEvents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(user))
        {
            var userFilter = user.Trim();
            if (Guid.TryParse(userFilter, out var actorId))
            {
                query = query.Where(item => item.ActorUserId == actorId);
            }
            else
            {
                var matchingUserIds = database.Users
                    .Where(account => account.Email != null
                        && account.Email.Contains(userFilter))
                    .Select(account => account.Id);
                query = query.Where(item => item.ActorUserId != null
                    && matchingUserIds.Contains(item.ActorUserId.Value));
            }
        }

        if (documentId is Guid id)
        {
            query = query.Where(item => item.DocumentId == id);
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            var actionFilter = action.Trim();
            query = query.Where(item => item.Action.Contains(actionFilter));
        }

        if (fromUtc is DateTimeOffset from)
        {
            query = query.Where(item => item.TimestampUtc >= from.UtcDateTime);
        }

        if (toUtc is DateTimeOffset to)
        {
            query = query.Where(item => item.TimestampUtc <= to.UtcDateTime);
        }

        if (!string.IsNullOrWhiteSpace(ipAddress))
        {
            var ipFilter = ipAddress.Trim();
            query = query.Where(item => item.IpAddress != null && item.IpAddress.Contains(ipFilter));
        }

        if (resultName is not null)
        {
            var result = Enum.Parse<AuditResult>(resultName, true);
            query = query.Where(item => item.Result == result);
        }

        return query;
    }

    private async Task<AdminAuditEventResponse[]> MapAuditEventsAsync(
        IReadOnlyList<AuditEvent> events,
        CancellationToken cancellationToken)
    {
        var actorIds = events.Where(item => item.ActorUserId.HasValue)
            .Select(item => item.ActorUserId!.Value)
            .Distinct()
            .ToArray();
        var emails = await database.Users
            .AsNoTracking()
            .Where(user => actorIds.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => user.Email, cancellationToken);

        return events.Select(item => new AdminAuditEventResponse(
                item.Id,
                item.ActorUserId,
                item.ActorUserId is Guid id ? emails.GetValueOrDefault(id) : null,
                item.ActorRole,
                item.DocumentId,
                item.Action,
                item.Result.ToString().ToUpperInvariant(),
                new DateTimeOffset(DateTime.SpecifyKind(item.TimestampUtc, DateTimeKind.Utc)),
                item.RequestId,
                item.IpAddress,
                item.UserAgent))
            .ToArray();
    }

    private bool TryValidateAuditFilters(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int page,
        int pageSize,
        string? outcome,
        out string? resultName)
    {
        resultName = null;
        if (fromUtc > toUtc)
        {
            ModelState.AddModelError(nameof(fromUtc), "'fromUtc' must not be later than 'toUtc'.");
        }

        if (!IsValidPage(page, pageSize))
        {
            ModelState.AddModelError(
                nameof(pageSize),
                $"Page must be positive and page size must be between 1 and {MaximumPageSize}.");
        }

        if (!string.IsNullOrWhiteSpace(outcome))
        {
            if (!Enum.TryParse<AuditResult>(outcome, true, out var parsed)
                || !Enum.IsDefined(parsed))
            {
                ModelState.AddModelError(nameof(outcome), "Outcome must be SUCCESS or DENIED.");
            }
            else
            {
                resultName = parsed.ToString();
            }
        }

        return ModelState.IsValid;
    }

    private static string CsvEscape(string? value)
    {
        var normalized = value ?? string.Empty;
        if (normalized.TrimStart().StartsWith('=') || normalized.TrimStart().StartsWith('+')
            || normalized.TrimStart().StartsWith('-') || normalized.TrimStart().StartsWith('@'))
        {
            normalized = "'" + normalized;
        }

        return $"\"{normalized.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static bool IsValidPage(int page, int pageSize) =>
        page >= 1
        && pageSize is >= 1 and <= MaximumPageSize
        && ((long)page - 1) * pageSize <= int.MaxValue;
}
