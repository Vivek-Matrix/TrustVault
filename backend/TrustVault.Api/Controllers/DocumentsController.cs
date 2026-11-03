using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TrustVault.Api.Models.Documents;
using TrustVault.Api.Services;
using TrustVault.Application.Documents;
using TrustVault.Domain.Audit;
using TrustVault.Domain.Documents;
using TrustVault.Infrastructure.Persistence;
using TrustVault.Infrastructure.Persistence.Entities;

namespace TrustVault.Api.Controllers;

[ApiController]
[Route("api/documents")]
public sealed class DocumentsController(
    ApplicationDbContext database,
    IPrivateDocumentStorage storage,
    DocumentAuditWriter auditWriter,
    ILogger<DocumentsController> logger) : ControllerBase
{
    private const long MaximumFileBytes = 10 * 1024 * 1024;
    private const long MaximumRequestBytes = MaximumFileBytes + 64 * 1024;

    [HttpPost]
    [RequestSizeLimit(MaximumRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaximumRequestBytes)]
    [ProducesResponseType<DocumentResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DocumentResponse>> Upload(
        [FromForm] UploadDocumentRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var ownerId))
        {
            return Unauthorized();
        }

        var file = request.File!;
        if (file.Length is <= 0 or > MaximumFileBytes)
        {
            ModelState.AddModelError(
                nameof(request.File),
                $"The PDF must be between 1 byte and {MaximumFileBytes} bytes.");
            return ValidationProblem(ModelState);
        }

        var originalFileName = Path.GetFileName(file.FileName.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(originalFileName)
            || originalFileName.Length > 255
            || originalFileName.Any(char.IsControl)
            || !string.Equals(
                Path.GetExtension(originalFileName),
                ".pdf",
                StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(request.File), "Only PDF files are accepted.");
            return ValidationProblem(ModelState);
        }

        await using var input = file.OpenReadStream();
        var signature = new byte[5];
        var signatureLength = await input.ReadAsync(signature, cancellationToken);
        if (signatureLength != signature.Length
            || !signature.AsSpan().SequenceEqual("%PDF-"u8))
        {
            ModelState.AddModelError(
                nameof(request.File),
                "The uploaded file does not have a valid PDF signature.");
            return ValidationProblem(ModelState);
        }

        if (!input.CanSeek)
        {
            throw new InvalidOperationException("The uploaded file stream must support seeking.");
        }

        input.Position = 0;
        var now = DateTime.UtcNow;
        var document = new Document
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            Title = request.Title.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description)
                ? null
                : request.Description.Trim(),
            Category = request.Category.Trim(),
            OriginalFileName = originalFileName,
            ContentType = "application/pdf",
            SizeBytes = file.Length,
            StorageKey = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        var stored = false;
        var committed = false;
        try
        {
            await storage.StoreAsync(document.StorageKey, input, cancellationToken);
            stored = true;

            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken);
            database.Documents.Add(document);
            await auditWriter.WriteAsync(
                HttpContext,
                document.Id,
                "document.upload",
                AuditResult.Success,
                JsonSerializer.Serialize(new { sizeBytes = document.SizeBytes }),
                cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            committed = true;
        }
        finally
        {
            if (stored && !committed)
            {
                await storage.DeleteAsync(document.StorageKey, CancellationToken.None);
            }
        }

        var response = ToResponse(document);
        return CreatedAtAction(nameof(GetMetadata), new { id = document.Id }, response);
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<DocumentResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DocumentResponse>>> List(
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var ownerId))
        {
            return Unauthorized();
        }

        var documents = await database.Documents
            .AsNoTracking()
            .Where(document => !document.IsDeleted
                && (document.OwnerId == ownerId
                    || document.Grants.Any(grant =>
                        grant.UserId == ownerId && grant.RevokedAtUtc == null)))
            .OrderByDescending(document => document.CreatedAtUtc)
            .Select(document => new
            {
                Document = document,
                IsOwner = document.OwnerId == ownerId,
                Permission = document.OwnerId == ownerId
                    ? null
                    : document.Grants
                        .Where(grant =>
                            grant.UserId == ownerId && grant.RevokedAtUtc == null)
                        .Select(grant => grant.Permission)
                        .FirstOrDefault() == DocumentPermission.Read
                            ? "READ"
                            : "EDIT"
            })
            .Select(item => new DocumentResponse(
                item.Document.Id,
                item.Document.Title,
                item.Document.Description,
                item.Document.Category,
                item.Document.OriginalFileName,
                item.Document.SizeBytes,
                item.Document.CreatedAtUtc,
                item.Document.UpdatedAtUtc,
                item.IsOwner,
                item.Permission))
            .ToListAsync(cancellationToken);

        await auditWriter.WriteAsync(
            HttpContext,
            null,
            "document.list",
            AuditResult.Success,
            "{}",
            cancellationToken);
        return Ok(documents);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<DocumentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentResponse>> GetMetadata(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var ownerId))
        {
            return Unauthorized();
        }

        var (document, permission) = await FindReadableDocumentAsync(
            id,
            ownerId,
            cancellationToken);
        if (document is null)
        {
            await RecordDeniedAccessAsync(id, "document.metadata.read", cancellationToken);
            return NotFound();
        }

        await auditWriter.WriteAsync(
            HttpContext,
            id,
            "document.metadata.read",
            AuditResult.Success,
            "{}",
            cancellationToken);
        return Ok(ToResponse(document, document.OwnerId == ownerId, permission));
    }

    [HttpGet("{id:guid}/content")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var ownerId))
        {
            return Unauthorized();
        }

        var (document, _) = await FindReadableDocumentAsync(
            id,
            ownerId,
            cancellationToken);
        if (document is null)
        {
            await RecordDeniedAccessAsync(id, "document.download", cancellationToken);
            return NotFound();
        }

        Stream content;
        try
        {
            content = await storage.OpenReadAsync(document.StorageKey, cancellationToken);
        }
        catch (FileNotFoundException exception)
        {
            logger.LogError(
                exception,
                "Stored document {DocumentId} is missing from private storage.",
                id);
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Document storage is temporarily unavailable.");
        }

        try
        {
            await auditWriter.WriteAsync(
                HttpContext,
                id,
                "document.download",
                AuditResult.Success,
                "{}",
                cancellationToken);
        }
        catch
        {
            await content.DisposeAsync();
            throw;
        }

        return File(content, document.ContentType, document.OriginalFileName);
    }

    [HttpGet("{id:guid}/grants")]
    [ProducesResponseType<IReadOnlyList<DocumentGrantResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<DocumentGrantResponse>>> GetGrants(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var ownerId))
        {
            return Unauthorized();
        }

        if (!await IsDocumentOwnerAsync(id, ownerId, cancellationToken))
        {
            await RecordDeniedAccessAsync(id, "document.grants.list", cancellationToken);
            return NotFound();
        }

        var grants = await database.DocumentGrants
            .AsNoTracking()
            .Where(grant => grant.DocumentId == id && grant.RevokedAtUtc == null)
            .Join(
                database.Users.AsNoTracking(),
                grant => grant.UserId,
                user => user.Id,
                (grant, user) => new
                {
                    grant.UserId,
                    Email = user.Email ?? string.Empty,
                    grant.Permission,
                    grant.GrantedAtUtc
                })
            .OrderBy(grant => grant.Email)
            .Select(grant => new DocumentGrantResponse(
                grant.UserId,
                grant.Email,
                grant.Permission == DocumentPermission.Read ? "READ" : "EDIT",
                grant.GrantedAtUtc))
            .ToListAsync(cancellationToken);

        await auditWriter.WriteAsync(
            HttpContext,
            id,
            "document.grants.list",
            AuditResult.Success,
            "{}",
            cancellationToken);
        return Ok(grants);
    }

    [HttpPost("{id:guid}/grants")]
    [ProducesResponseType<DocumentGrantResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentGrantResponse>> GrantAccess(
        Guid id,
        GrantDocumentAccessRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var ownerId))
        {
            return Unauthorized();
        }

        if (!Enum.TryParse<DocumentPermission>(request.Permission, true, out var permission)
            || !Enum.IsDefined(permission))
        {
            ModelState.AddModelError(
                nameof(request.Permission),
                "Permission must be READ or EDIT.");
            return ValidationProblem(ModelState);
        }

        await using var transaction = await database.Database.BeginTransactionAsync(
            cancellationToken);
        await LockAuditChainAsync(cancellationToken);

        if (!await IsDocumentOwnerAsync(id, ownerId, cancellationToken))
        {
            await RecordDeniedAccessAsync(id, "document.grant", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return NotFound();
        }

        var normalizedEmail = request.Email.Trim().ToUpperInvariant();
        var recipient = await database.Users
            .FirstOrDefaultAsync(
                user => user.NormalizedEmail == normalizedEmail && !user.IsSuspended,
                cancellationToken);
        if (recipient is null || recipient.Id == ownerId)
        {
            await RecordDeniedAccessAsync(id, "document.grant", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return NotFound();
        }

        var now = DateTime.UtcNow;
        var activeGrant = await database.DocumentGrants
            .FirstOrDefaultAsync(
                grant => grant.DocumentId == id
                    && grant.UserId == recipient.Id
                    && grant.RevokedAtUtc == null,
                cancellationToken);

        if (activeGrant is not null && activeGrant.Permission != permission)
        {
            activeGrant.RevokedAtUtc = now;
            activeGrant.RevocationReason = "Permission changed by document owner";
            await database.SaveChangesAsync(cancellationToken);
            activeGrant = null;
        }

        if (activeGrant is null)
        {
            activeGrant = new DocumentGrant
            {
                Id = Guid.NewGuid(),
                DocumentId = id,
                UserId = recipient.Id,
                Permission = permission,
                GrantedByUserId = ownerId,
                GrantedAtUtc = now
            };
            database.DocumentGrants.Add(activeGrant);
        }

        await auditWriter.WriteAsync(
            HttpContext,
            id,
            "document.grant",
            AuditResult.Success,
            JsonSerializer.Serialize(new
            {
                recipientUserId = recipient.Id,
                permission = permission.ToString().ToUpperInvariant()
            }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var response = new DocumentGrantResponse(
            recipient.Id,
            recipient.Email ?? string.Empty,
            activeGrant.Permission.ToString().ToUpperInvariant(),
            activeGrant.GrantedAtUtc);
        return CreatedAtAction(nameof(GetGrants), new { id }, response);
    }

    [HttpDelete("{id:guid}/grants/{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeAccess(
        Guid id,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var ownerId))
        {
            return Unauthorized();
        }

        await using var transaction = await database.Database.BeginTransactionAsync(
            cancellationToken);
        await LockAuditChainAsync(cancellationToken);

        if (!await IsDocumentOwnerAsync(id, ownerId, cancellationToken))
        {
            await RecordDeniedAccessAsync(id, "document.grant.revoke", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return NotFound();
        }

        var activeGrant = await database.DocumentGrants
            .FirstOrDefaultAsync(
                grant => grant.DocumentId == id
                    && grant.UserId == userId
                    && grant.RevokedAtUtc == null,
                cancellationToken);
        if (activeGrant is null)
        {
            await RecordDeniedAccessAsync(id, "document.grant.revoke", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return NotFound();
        }

        activeGrant.RevokedAtUtc = DateTime.UtcNow;
        activeGrant.RevocationReason = "Revoked by document owner";
        await auditWriter.WriteAsync(
            HttpContext,
            id,
            "document.grant.revoke",
            AuditResult.Success,
            JsonSerializer.Serialize(new { recipientUserId = userId }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    private async Task<(Document? Document, DocumentPermission? Permission)>
        FindReadableDocumentAsync(
            Guid documentId,
            Guid userId,
            CancellationToken cancellationToken)
    {
        var document = await database.Documents
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.Id == documentId && !item.IsDeleted,
                cancellationToken);
        if (document is null || document.OwnerId == userId)
        {
            return (document, null);
        }

        var permission = await database.DocumentGrants
            .AsNoTracking()
            .Where(grant => grant.DocumentId == documentId
                && grant.UserId == userId
                && grant.RevokedAtUtc == null)
            .Select(grant => (DocumentPermission?)grant.Permission)
            .FirstOrDefaultAsync(cancellationToken);
        return permission is null ? (null, null) : (document, permission);
    }

    private Task<bool> IsDocumentOwnerAsync(
        Guid documentId,
        Guid ownerId,
        CancellationToken cancellationToken) =>
        database.Documents.AnyAsync(
            document => document.Id == documentId
                && document.OwnerId == ownerId
                && !document.IsDeleted,
            cancellationToken);

    private Task<int> LockAuditChainAsync(CancellationToken cancellationToken) =>
        database.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(18348721, 9283147)",
            cancellationToken);

    private async Task RecordDeniedAccessAsync(
        Guid documentId,
        string action,
        CancellationToken cancellationToken)
    {
        await auditWriter.WriteAsync(
            HttpContext,
            documentId,
            action,
            AuditResult.Denied,
            "{}",
            cancellationToken);
    }

    private bool TryGetUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);

    private static DocumentResponse ToResponse(
        Document document,
        bool isOwner = true,
        DocumentPermission? permission = null) =>
        new(
            document.Id,
            document.Title,
            document.Description,
            document.Category,
            document.OriginalFileName,
            document.SizeBytes,
            document.CreatedAtUtc,
            document.UpdatedAtUtc,
            isOwner,
            permission?.ToString().ToUpperInvariant());
}
