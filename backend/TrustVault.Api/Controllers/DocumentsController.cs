using System.IO.Compression;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Net.Sockets;
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
    IPrivateDocumentScanner scanner,
    DocumentAuditWriter auditWriter,
    ILogger<DocumentsController> logger,
    IDocumentTextConverter textConverter) : ControllerBase
{
    private const long MaximumFileBytes = 10 * 1024 * 1024;
    private const long MaximumRequestBytes = MaximumFileBytes + 64 * 1024;
    private const int MaximumEditableTextCharacters = 2_000_000;

    private static async Task<(string? ContentType, string? Error)> ValidateDocumentContentAsync(
        string fileName,
        Stream content,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(fileName);
        if (!content.CanSeek)
        {
            throw new InvalidOperationException("The uploaded file stream must support seeking.");
        }

        content.Position = 0;
        switch (extension.ToLowerInvariant())
        {
            case ".pdf":
                if (!await HasSignatureAsync(content, "%PDF-"u8.ToArray(), cancellationToken))
                {
                    return (null, "The uploaded file does not have a valid PDF signature.");
                }

                return ("application/pdf", null);
            case ".txt":
                try
                {
                    using (var reader = new StreamReader(
                        content,
                        new UTF8Encoding(false, true),
                        detectEncodingFromByteOrderMarks: true,
                        leaveOpen: true))
                    {
                        var buffer = new char[4096];
                        while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken) > 0)
                        {
                        }
                    }
                }
                catch (DecoderFallbackException)
                {
                    return (null, "Text files must use UTF-8 or a Unicode encoding with a byte-order mark.");
                }

                return ("text/plain; charset=utf-8", null);
            case ".doc":
                if (!await HasSignatureAsync(
                    content,
                    [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1],
                    cancellationToken))
                {
                    return (null, "The uploaded file does not have a valid Word document signature.");
                }

                return ("application/msword", null);
            case ".docx":
                if (!IsValidDocxPackage(content))
                {
                    return (null, "The uploaded file is not a valid .docx Word document.");
                }

                return (
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    null);
            default:
                return (null, "Supported file types are PDF, TXT, DOC, and DOCX.");
        }
    }

    private static async Task<bool> HasSignatureAsync(
        Stream content,
        byte[] expectedSignature,
        CancellationToken cancellationToken)
    {
        var signature = new byte[expectedSignature.Length];
        var signatureLength = 0;
        while (signatureLength < signature.Length)
        {
            var bytesRead = await content.ReadAsync(
                signature.AsMemory(signatureLength),
                cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            signatureLength += bytesRead;
        }

        content.Position = 0;
        return signatureLength == signature.Length
            && signature.AsSpan().SequenceEqual(expectedSignature);
    }

    private static bool IsValidDocxPackage(Stream content)
    {
        try
        {
            using var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
            var contentTypes = archive.GetEntry("[Content_Types].xml");
            var document = archive.GetEntry("word/document.xml");
            return contentTypes is not null
                && document is not null
                && HasXmlRoot(
                    contentTypes,
                    "Types",
                    "http://schemas.openxmlformats.org/package/2006/content-types")
                && HasXmlRoot(
                    document,
                    "document",
                    "http://schemas.openxmlformats.org/wordprocessingml/2006/main");
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or XmlException)
        {
            return false;
        }
    }

    private static bool HasXmlRoot(ZipArchiveEntry entry, string localName, string namespaceUri)
    {
        using var entryStream = entry.Open();
        using var reader = XmlReader.Create(
            entryStream,
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersInDocument = MaximumFileBytes,
                XmlResolver = null
            });
        var validRoot = reader.MoveToContent() == XmlNodeType.Element
            && reader.LocalName == localName
            && reader.NamespaceURI == namespaceUri;
        while (reader.Read())
        {
        }

        return validRoot;
    }

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
                $"The file must be between 1 byte and {MaximumFileBytes} bytes.");
            return ValidationProblem(ModelState);
        }

        var originalFileName = Path.GetFileName(file.FileName.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(originalFileName)
            || originalFileName.Length > 255
            || originalFileName.Any(char.IsControl))
        {
            ModelState.AddModelError(nameof(request.File), "The file name is invalid.");
            return ValidationProblem(ModelState);
        }

        await using var input = file.OpenReadStream();
        var (contentType, validationError) = await ValidateDocumentContentAsync(
            originalFileName,
            input,
            cancellationToken);
        if (validationError is not null)
        {
            ModelState.AddModelError(
                nameof(request.File),
                validationError);
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
            ContentType = contentType!,
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
            document.ScanAttemptCount = 1;
            document.ScanStatus = await ScanStoredDocumentAsync(
                document.Id,
                document.StorageKey,
                cancellationToken);
            document.LastScanAtUtc = DateTime.UtcNow;

            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken);
            database.Documents.Add(document);
            await auditWriter.WriteAsync(
                HttpContext,
                document.Id,
                "document.upload",
                document.ScanStatus == DocumentScanStatus.Clean
                    ? AuditResult.Success
                    : AuditResult.Denied,
                JsonSerializer.Serialize(new
                {
                    sizeBytes = document.SizeBytes,
                    scanStatus = ScanStatusName(document.ScanStatus)
                }),
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
        [FromQuery] string? search,
        [FromQuery] string? category,
        [FromQuery] string? owner,
        [FromQuery] DateTime? createdFrom,
        [FromQuery] DateTime? createdTo,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var ownerId))
        {
            return Unauthorized();
        }

        var trimmedSearch = search?.Trim();
        var trimmedCategory = category?.Trim();
        var trimmedOwner = owner?.Trim();

        var documentsQuery = database.Documents
            .AsNoTracking()
            .Where(document => !document.IsDeleted
                && (document.OwnerId == ownerId
                    || document.Grants.Any(grant =>
                        grant.UserId == ownerId && grant.RevokedAtUtc == null)))
            .Join(
                database.Users.AsNoTracking(),
                document => document.OwnerId,
                user => user.Id,
                (document, user) => new
                {
                    Document = document,
                    OwnerEmail = user.Email ?? string.Empty,
                    OwnerUserName = user.UserName ?? string.Empty
                });

        if (!string.IsNullOrWhiteSpace(trimmedSearch))
        {
            var searchValue = trimmedSearch;
            documentsQuery = documentsQuery.Where(item =>
                item.Document.Title.Contains(searchValue)
                || item.Document.Category.Contains(searchValue)
                || item.Document.OriginalFileName.Contains(searchValue)
                || item.OwnerEmail.Contains(searchValue)
                || item.OwnerUserName.Contains(searchValue));
        }

        if (!string.IsNullOrWhiteSpace(trimmedCategory))
        {
            var categoryValue = trimmedCategory;
            documentsQuery = documentsQuery.Where(item =>
                item.Document.Category.Contains(categoryValue));
        }

        if (!string.IsNullOrWhiteSpace(trimmedOwner))
        {
            var ownerValue = trimmedOwner;
            documentsQuery = documentsQuery.Where(item =>
                item.OwnerEmail.Contains(ownerValue)
                || item.OwnerUserName.Contains(ownerValue));
        }

        if (createdFrom is not null)
        {
            documentsQuery = documentsQuery.Where(item => item.Document.CreatedAtUtc >= createdFrom.Value);
        }

        if (createdTo is not null)
        {
            documentsQuery = documentsQuery.Where(item => item.Document.CreatedAtUtc <= createdTo.Value);
        }

        var documents = await documentsQuery
            .OrderByDescending(item => item.Document.CreatedAtUtc)
            .Select(item => new
            {
                item.Document,
                item.OwnerEmail,
                item.OwnerUserName,
                IsOwner = item.Document.OwnerId == ownerId,
                Permission = item.Document.OwnerId == ownerId
                    ? null
                    : item.Document.Grants
                        .Where(grant =>
                            grant.UserId == ownerId && grant.RevokedAtUtc == null)
                        .Select(grant => grant.Permission)
                        .FirstOrDefault() == DocumentPermission.Read
                            ? "READ"
                            : "EDIT",
                item.Document.ScanStatus,
                item.Document.ScanAttemptCount,
                item.Document.LastScanAtUtc
            })
            .ToListAsync(cancellationToken);
        var response = documents.Select(item => new DocumentResponse(
                item.Document.Id,
                item.Document.Title,
                item.Document.Description,
                item.Document.Category,
                item.Document.OriginalFileName,
                item.Document.SizeBytes,
                item.Document.CreatedAtUtc,
                item.Document.UpdatedAtUtc,
                item.IsOwner,
                item.Permission,
                ScanStatusName(item.ScanStatus),
                item.ScanAttemptCount,
                item.LastScanAtUtc))
            .ToArray();

        await auditWriter.WriteAsync(
            HttpContext,
            null,
            "document.list",
            AuditResult.Success,
            JsonSerializer.Serialize(new
            {
                search = trimmedSearch,
                category = trimmedCategory,
                owner = trimmedOwner,
                createdFrom = createdFrom,
                createdTo = createdTo
            }),
            cancellationToken);
        return Ok(response);
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

    [HttpGet("{id:guid}/audits")]
    [ProducesResponseType<IReadOnlyList<DocumentAuditResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<DocumentAuditResponse>>> GetAudits(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        if ((await FindReadableDocumentAsync(id, userId, cancellationToken)).Document is null)
        {
            await RecordDeniedAccessAsync(id, "document.audit.read", cancellationToken);
            return NotFound();
        }

        var auditEvents = await database.AuditEvents
            .AsNoTracking()
            .Where(auditEvent => auditEvent.DocumentId == id
                && (auditEvent.Action == "document.upload"
                    || auditEvent.Action == "document.metadata.read"
                    || auditEvent.Action == "document.download"
                    || auditEvent.Action == "document.text.read"
                    || auditEvent.Action == "document.versions.list"
                    || auditEvent.Action == "document.version.download"
                    || auditEvent.Action == "document.update"
                    || auditEvent.Action == "document.text.update"
                    || auditEvent.Action == "document.content.replace"
                    || auditEvent.Action == "document.scan.retry"
                    || auditEvent.Action == "document.scan.version.retry"
                    || auditEvent.Action == "document.text.update.scan"
                    || auditEvent.Action == "document.content.replace.scan"))
            .OrderByDescending(auditEvent => auditEvent.TimestampUtc)
            .ThenByDescending(auditEvent => auditEvent.Id)
            .Select(auditEvent => new
            {
                auditEvent.Id,
                auditEvent.ActorUserId,
                auditEvent.Action,
                auditEvent.Result,
                auditEvent.TimestampUtc
            })
            .ToListAsync(cancellationToken);

        var actorIds = auditEvents
            .Where(auditEvent => auditEvent.ActorUserId.HasValue)
            .Select(auditEvent => auditEvent.ActorUserId!.Value)
            .Distinct()
            .ToArray();
        var actorEmails = await database.Users
            .AsNoTracking()
            .Where(user => actorIds.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => user.Email, cancellationToken);

        var response = auditEvents
            .Select(auditEvent => new DocumentAuditResponse(
                auditEvent.Id,
                auditEvent.ActorUserId is Guid actorId
                    && actorEmails.TryGetValue(actorId, out var email)
                        ? email
                        : null,
                auditEvent.Action == "document.upload"
                    ? "CREATED"
                    : auditEvent.Action is "document.update"
                        or "document.text.update"
                        or "document.content.replace"
                        ? "EDITED"
                        : auditEvent.Action is "document.scan.retry"
                            or "document.scan.version.retry"
                            or "document.text.update.scan"
                            or "document.content.replace.scan"
                            ? "SCANNED"
                            : "READ",
                auditEvent.Result.ToString().ToUpperInvariant(),
                auditEvent.TimestampUtc))
            .ToArray();

        return Ok(response);
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
        if (document.ScanStatus != DocumentScanStatus.Clean)
        {
            return await ScanBlockedAsync(id, "document.download", document.ScanStatus, cancellationToken);
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

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteDocument(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var ownerId))
        {
            return Unauthorized();
        }

        var storageKeysToDelete = new List<string>();
        await using (var transaction = await database.Database.BeginTransactionAsync(
            cancellationToken))
        {
            await LockAuditChainAsync(cancellationToken);
            var document = await database.Documents
                .FirstOrDefaultAsync(
                    item => item.Id == id && item.OwnerId == ownerId && !item.IsDeleted,
                    cancellationToken);
            if (document is null)
            {
                await RecordDeniedAccessAsync(id, "document.delete", cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return NotFound();
            }

            var versions = await database.DocumentVersions
                .Where(version => version.DocumentId == id)
                .ToListAsync(cancellationToken);
            storageKeysToDelete.Add(document.StorageKey);
            storageKeysToDelete.AddRange(versions.Select(version => version.StorageKey));
            database.DocumentVersions.RemoveRange(versions);

            var now = DateTime.UtcNow;
            var activeGrants = await database.DocumentGrants
                .Where(grant => grant.DocumentId == id && grant.RevokedAtUtc == null)
                .ToListAsync(cancellationToken);
            foreach (var grant in activeGrants)
            {
                grant.RevokedAtUtc = now;
                grant.RevocationReason = "Document removed by owner";
            }

            document.IsDeleted = true;
            document.UpdatedAtUtc = now;
            await auditWriter.WriteAsync(
                HttpContext,
                id,
                "document.delete",
                AuditResult.Success,
                JsonSerializer.Serialize(new
                {
                    revokedGrantCount = activeGrants.Count
                }),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        var storageCleanupFailed = false;
        foreach (var storageKey in storageKeysToDelete.Distinct(StringComparer.Ordinal))
        {
            try
            {
                await storage.DeleteAsync(storageKey, CancellationToken.None);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                logger.LogError(
                    exception,
                    "Could not remove stored content for deleted document {DocumentId}.",
                    id);
                storageCleanupFailed = true;
            }
        }

        if (storageCleanupFailed)
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "The document was removed, but some stored file data could not be deleted.",
                detail: "The document and its access grants are unavailable. Contact the application owner to complete storage cleanup.");
        }

        return NoContent();
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<DocumentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentResponse>> UpdateMetadata(
        Guid id,
        UpdateDocumentRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        await using var transaction = await database.Database.BeginTransactionAsync(
            cancellationToken);
        await LockAuditChainAsync(cancellationToken);
        var (document, permission) = await FindReadableDocumentAsync(
            id,
            userId,
            cancellationToken);
        if (document is null
            || (document.OwnerId != userId && permission != DocumentPermission.Edit))
        {
            await RecordDeniedAccessAsync(id, "document.update", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return NotFound();
        }
        if (document.ScanStatus != DocumentScanStatus.Clean)
        {
            await RecordDeniedAccessAsync(id, "document.update", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ScanBlocked(document.ScanStatus);
        }

        var trackedDocument = await database.Documents
            .FirstAsync(item => item.Id == id && !item.IsDeleted, cancellationToken);
        trackedDocument.Title = request.Title.Trim();
        trackedDocument.Description = string.IsNullOrWhiteSpace(request.Description)
            ? null
            : request.Description.Trim();
        trackedDocument.Category = request.Category.Trim();
        trackedDocument.UpdatedAtUtc = DateTime.UtcNow;
        await auditWriter.WriteAsync(
            HttpContext,
            id,
            "document.update",
            AuditResult.Success,
            JsonSerializer.Serialize(new { fields = new[] { "title", "description", "category" } }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Ok(ToResponse(trackedDocument, document.OwnerId == userId, permission));
    }

    [HttpGet("{id:guid}/editable-text")]
    [ProducesResponseType<DocumentEditableTextResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentEditableTextResponse>> GetEditableText(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        var (document, permission) = await FindReadableDocumentAsync(id, userId, cancellationToken);
        if (document is null
            || (document.OwnerId != userId && permission != DocumentPermission.Edit)
            || !IsTextEditableExtension(Path.GetExtension(document.OriginalFileName)))
        {
            await RecordDeniedAccessAsync(id, "document.text.read", cancellationToken);
            return NotFound();
        }
        if (document.ScanStatus != DocumentScanStatus.Clean)
        {
            return await ScanBlockedAsync(id, "document.text.read", document.ScanStatus, cancellationToken);
        }

        Stream content;
        try
        {
            content = await storage.OpenReadAsync(document.StorageKey, cancellationToken);
        }
        catch (FileNotFoundException exception)
        {
            logger.LogError(exception, "Stored document {DocumentId} is missing.", id);
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Document storage is temporarily unavailable.");
        }

        string text;
        await using (content)
        {
            var extension = Path.GetExtension(document.OriginalFileName);
            if (string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(
                    content,
                    new UTF8Encoding(false, true),
                    detectEncodingFromByteOrderMarks: true,
                    leaveOpen: true);
                text = await reader.ReadToEndAsync(cancellationToken);
            }
            else
            {
                try
                {
                    text = await textConverter.ExtractTextAsync(
                        content,
                        extension,
                        cancellationToken);
                }
                catch (DocumentConversionUnavailableException exception)
                {
                    logger.LogError(exception, "Word conversion is unavailable.");
                    return Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Word editing is temporarily unavailable.",
                        detail: exception.Message);
                }
                catch (DocumentConversionException exception)
                {
                    logger.LogWarning(exception, "Could not read Word document {DocumentId}.", id);
                    return Problem(
                        statusCode: StatusCodes.Status422UnprocessableEntity,
                        title: "This Word file could not be converted for editing.",
                        detail: exception.Message);
                }
            }
        }

        if (text.Length > MaximumEditableTextCharacters)
        {
            return Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "This document contains too much text to edit in the browser.");
        }

        await auditWriter.WriteAsync(
            HttpContext,
            id,
            "document.text.read",
            AuditResult.Success,
            "{}",
            cancellationToken);
        return Ok(new DocumentEditableTextResponse(text));
    }

    [HttpPut("{id:guid}/editable-text")]
    [RequestSizeLimit(MaximumRequestBytes)]
    [ProducesResponseType<DocumentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentResponse>> UpdateEditableText(
        Guid id,
        UpdateDocumentTextRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        var (document, permission) = await FindReadableDocumentAsync(id, userId, cancellationToken);
        if (document is null
            || (document.OwnerId != userId && permission != DocumentPermission.Edit)
            || !IsTextEditableExtension(Path.GetExtension(document.OriginalFileName)))
        {
            await RecordDeniedAccessAsync(id, "document.text.update", cancellationToken);
            return NotFound();
        }
        if (document.ScanStatus != DocumentScanStatus.Clean)
        {
            return await ScanBlockedAsync(id, "document.text.update", document.ScanStatus, cancellationToken);
        }

        if (request.Text is null)
        {
            ModelState.AddModelError(nameof(request.Text), "Text content is required.");
            return ValidationProblem(ModelState);
        }

        if (request.Text.Length > MaximumEditableTextCharacters)
        {
            ModelState.AddModelError(
                nameof(request.Text),
                "The edited text is too long to save.");
            return ValidationProblem(ModelState);
        }

        var sourceExtension = Path.GetExtension(document.OriginalFileName);
        var isWordDocument = !string.Equals(
            sourceExtension,
            ".txt",
            StringComparison.OrdinalIgnoreCase);
        byte[] outputBytes;
        if (isWordDocument)
        {
            try
            {
                outputBytes = await textConverter.CreateDocxAsync(
                    request.Text,
                    cancellationToken);
            }
            catch (DocumentConversionUnavailableException exception)
            {
                logger.LogError(exception, "Word conversion is unavailable.");
                return Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Word editing is temporarily unavailable.",
                    detail: exception.Message);
            }
            catch (DocumentConversionException exception)
            {
                logger.LogWarning(exception, "Could not create edited Word document {DocumentId}.", id);
                return Problem(
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "The edited text could not be converted to DOCX.",
                    detail: exception.Message);
            }
        }
        else
        {
            outputBytes = new UTF8Encoding(false).GetBytes(request.Text);
        }
        if (outputBytes.Length <= 0 || outputBytes.LongLength > MaximumFileBytes)
        {
            ModelState.AddModelError(
                nameof(request.Text),
                $"The edited file must be between 1 byte and {MaximumFileBytes} bytes.");
            return ValidationProblem(ModelState);
        }

        var outputFileName = isWordDocument
            ? $"{Path.GetFileNameWithoutExtension(document.OriginalFileName)}.docx"
            : document.OriginalFileName;
        var outputContentType = isWordDocument
            ? "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
            : "text/plain; charset=utf-8";
        var originalStorageKey = document.StorageKey;
        var trackedDocument = await database.Documents
            .FirstAsync(item => item.Id == id && !item.IsDeleted, cancellationToken);
        var newStorageKey = Guid.NewGuid().ToString("N");
        var stored = false;
        var committed = false;

        try
        {
            await using var outputStream = new MemoryStream(outputBytes, writable: false);
            await storage.StoreAsync(newStorageKey, outputStream, cancellationToken);
            stored = true;
            var scanStatus = await ScanStoredDocumentAsync(
                id,
                newStorageKey,
                cancellationToken);
            if (scanStatus != DocumentScanStatus.Clean)
            {
                await RecordScanCandidateDeniedAsync(
                    id,
                    "document.text.update.scan",
                    scanStatus,
                    cancellationToken);
                return ScanCandidateFailure(scanStatus);
            }

            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken);
            await LockAuditChainAsync(cancellationToken);
            var (latestDocument, latestPermission) = await FindReadableDocumentAsync(
                id,
                userId,
                cancellationToken);
            if (latestDocument is null
                || (latestDocument.OwnerId != userId
                    && latestPermission != DocumentPermission.Edit))
            {
                await RecordDeniedAccessAsync(id, "document.text.update", cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return NotFound();
            }

            await database.Entry(trackedDocument).ReloadAsync(cancellationToken);
            if (!string.Equals(
                trackedDocument.StorageKey,
                originalStorageKey,
                StringComparison.Ordinal))
            {
                return Conflict(new ProblemDetails
                {
                    Title = "This document changed while you were editing it.",
                    Status = StatusCodes.Status409Conflict
                });
            }

            var previousVersion = await database.DocumentVersions
                .Where(version => version.DocumentId == id)
                .Select(version => (int?)version.VersionNumber)
                .MaxAsync(cancellationToken) ?? 0;
            database.DocumentVersions.Add(new DocumentVersion
            {
                Id = Guid.NewGuid(),
                DocumentId = id,
                VersionNumber = previousVersion + 1,
                StorageKey = trackedDocument.StorageKey,
                OriginalFileName = trackedDocument.OriginalFileName,
                ContentType = trackedDocument.ContentType,
                SizeBytes = trackedDocument.SizeBytes,
                UploadedByUserId = trackedDocument.OwnerId,
                CreatedAtUtc = trackedDocument.UpdatedAtUtc,
                ScanStatus = trackedDocument.ScanStatus,
                ScanAttemptCount = trackedDocument.ScanAttemptCount,
                LastScanAtUtc = trackedDocument.LastScanAtUtc
            });

            trackedDocument.StorageKey = newStorageKey;
            trackedDocument.OriginalFileName = outputFileName;
            trackedDocument.ContentType = outputContentType;
            trackedDocument.SizeBytes = outputBytes.Length;
            trackedDocument.ScanStatus = scanStatus;
            trackedDocument.ScanAttemptCount = 1;
            trackedDocument.LastScanAtUtc = DateTime.UtcNow;
            trackedDocument.UpdatedAtUtc = DateTime.UtcNow;
            await auditWriter.WriteAsync(
                HttpContext,
                id,
                "document.text.update",
                AuditResult.Success,
                JsonSerializer.Serialize(new
                {
                    sourceExtension,
                    outputExtension = Path.GetExtension(outputFileName),
                    sizeBytes = outputBytes.Length,
                    scanStatus = ScanStatusName(scanStatus)
                }),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            committed = true;
        }
        finally
        {
            if (stored && !committed)
            {
                await storage.DeleteAsync(newStorageKey, CancellationToken.None);
            }
        }

        return Ok(ToResponse(trackedDocument, document.OwnerId == userId, permission));
    }

    [HttpPut("{id:guid}/content")]
    [RequestSizeLimit(MaximumRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaximumRequestBytes)]
    [ProducesResponseType<DocumentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentResponse>> ReplaceContent(
        Guid id,
        [FromForm] IFormFile? file,
        [FromForm] bool purgePreviousVersions,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        var (document, permission) = await FindReadableDocumentAsync(
            id,
            userId,
            cancellationToken);
        if (document is null
            || (document.OwnerId != userId && permission != DocumentPermission.Edit))
        {
            await RecordDeniedAccessAsync(id, "document.content.replace", cancellationToken);
            return NotFound();
        }
        if (document.ScanStatus != DocumentScanStatus.Clean)
        {
            return await ScanBlockedAsync(id, "document.content.replace", document.ScanStatus, cancellationToken);
        }

        if (file is null || file.Length is <= 0 or > MaximumFileBytes)
        {
            ModelState.AddModelError(
                nameof(file),
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
            ModelState.AddModelError(nameof(file), "Only PDF files are accepted.");
            return ValidationProblem(ModelState);
        }

        await using var input = file.OpenReadStream();
        var signature = new byte[5];
        var signatureLength = await input.ReadAsync(signature, cancellationToken);
        if (signatureLength != signature.Length
            || !signature.AsSpan().SequenceEqual("%PDF-"u8))
        {
            ModelState.AddModelError(
                nameof(file),
                "The uploaded file does not have a valid PDF signature.");
            return ValidationProblem(ModelState);
        }

        if (!input.CanSeek)
        {
            throw new InvalidOperationException("The uploaded file stream must support seeking.");
        }

        input.Position = 0;
        var trackedDocument = await database.Documents
            .FirstAsync(item => item.Id == id && !item.IsDeleted, cancellationToken);
        var newStorageKey = Guid.NewGuid().ToString("N");
        var stored = false;
        var committed = false;
        var cleanupFailed = false;
        var storageKeysToPurge = new List<string>();
        try
        {
            await storage.StoreAsync(newStorageKey, input, cancellationToken);
            stored = true;
            var scanStatus = await ScanStoredDocumentAsync(
                id,
                newStorageKey,
                cancellationToken);
            if (scanStatus != DocumentScanStatus.Clean)
            {
                await RecordScanCandidateDeniedAsync(
                    id,
                    "document.content.replace.scan",
                    scanStatus,
                    cancellationToken);
                return ScanCandidateFailure(scanStatus);
            }

            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken);
            await LockAuditChainAsync(cancellationToken);
            var (latestDocument, latestPermission) = await FindReadableDocumentAsync(
                id,
                userId,
                cancellationToken);
            if (latestDocument is null
                || (latestDocument.OwnerId != userId
                    && latestPermission != DocumentPermission.Edit))
            {
                await RecordDeniedAccessAsync(id, "document.content.replace", cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return NotFound();
            }

            permission = latestPermission;
            await database.Entry(trackedDocument).ReloadAsync(cancellationToken);
            var previousStorageKey = trackedDocument.StorageKey;
            var priorVersions = await database.DocumentVersions
                .Where(version => version.DocumentId == id)
                .OrderBy(version => version.VersionNumber)
                .ToListAsync(cancellationToken);
            var previousVersion = priorVersions
                .Select(version => (int?)version.VersionNumber)
                .Max() ?? 0;
            if (purgePreviousVersions)
            {
                storageKeysToPurge.Add(previousStorageKey);
                storageKeysToPurge.AddRange(priorVersions.Select(version => version.StorageKey));
                database.DocumentVersions.RemoveRange(priorVersions);
            }
            else
            {
                database.DocumentVersions.Add(new DocumentVersion
                {
                    Id = Guid.NewGuid(),
                    DocumentId = id,
                    VersionNumber = previousVersion + 1,
                    StorageKey = previousStorageKey,
                    OriginalFileName = trackedDocument.OriginalFileName,
                    ContentType = trackedDocument.ContentType,
                    SizeBytes = trackedDocument.SizeBytes,
                    UploadedByUserId = trackedDocument.OwnerId,
                    CreatedAtUtc = trackedDocument.UpdatedAtUtc,
                    ScanStatus = trackedDocument.ScanStatus,
                    ScanAttemptCount = trackedDocument.ScanAttemptCount,
                    LastScanAtUtc = trackedDocument.LastScanAtUtc
                });
            }

            trackedDocument.StorageKey = newStorageKey;
            trackedDocument.OriginalFileName = originalFileName;
            trackedDocument.ContentType = "application/pdf";
            trackedDocument.SizeBytes = file.Length;
            trackedDocument.ScanStatus = scanStatus;
            trackedDocument.ScanAttemptCount = 1;
            trackedDocument.LastScanAtUtc = DateTime.UtcNow;
            trackedDocument.UpdatedAtUtc = DateTime.UtcNow;
            await auditWriter.WriteAsync(
                HttpContext,
                id,
                "document.content.replace",
                AuditResult.Success,
                JsonSerializer.Serialize(new
                {
                    sizeBytes = file.Length,
                    scanStatus = ScanStatusName(scanStatus),
                    previousVersion = purgePreviousVersions ? (int?)null : previousVersion + 1,
                    purgedPreviousVersions = purgePreviousVersions
                }),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            committed = true;

            foreach (var storageKey in storageKeysToPurge.Distinct(StringComparer.Ordinal))
            {
                try
                {
                    await storage.DeleteAsync(storageKey, CancellationToken.None);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogError(
                        exception,
                        "Could not remove previous document content for {DocumentId}.",
                        id);
                    cleanupFailed = true;
                }
            }
        }
        finally
        {
            if (stored && !committed)
            {
                await storage.DeleteAsync(newStorageKey, CancellationToken.None);
            }
        }

        if (cleanupFailed)
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "The PDF was saved, but previous files could not all be removed.",
                detail: "Do not treat this redaction as complete. Contact the application owner.");
        }

        return Ok(ToResponse(trackedDocument, document.OwnerId == userId, permission));
    }

    [HttpGet("{id:guid}/versions")]
    [ProducesResponseType<IReadOnlyList<DocumentVersionResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<DocumentVersionResponse>>> ListVersions(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        if ((await FindReadableDocumentAsync(id, userId, cancellationToken)).Document is null)
        {
            await RecordDeniedAccessAsync(id, "document.versions.list", cancellationToken);
            return NotFound();
        }

        var versions = await database.DocumentVersions
            .AsNoTracking()
            .Where(version => version.DocumentId == id)
            .OrderByDescending(version => version.VersionNumber)
            .Select(version => new
            {
                version.VersionNumber,
                version.OriginalFileName,
                version.SizeBytes,
                version.CreatedAtUtc,
                version.ScanStatus,
                version.ScanAttemptCount,
                version.LastScanAtUtc
            })
            .ToListAsync(cancellationToken);
        var response = versions.Select(version => new DocumentVersionResponse(
            version.VersionNumber,
            version.OriginalFileName,
            version.SizeBytes,
            version.CreatedAtUtc,
            ScanStatusName(version.ScanStatus),
            version.ScanAttemptCount,
            version.LastScanAtUtc)).ToArray();
        await auditWriter.WriteAsync(
            HttpContext,
            id,
            "document.versions.list",
            AuditResult.Success,
            "{}",
            cancellationToken);
        return Ok(response);
    }

    [HttpPost("{id:guid}/scan/retry")]
    [ProducesResponseType<DocumentScanStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DocumentScanStatusResponse>> RetryDocumentScan(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var ownerId))
        {
            return Unauthorized();
        }

        var document = await database.Documents
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.Id == id && item.OwnerId == ownerId && !item.IsDeleted,
                cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        var canRetry = document.ScanStatus is DocumentScanStatus.PendingScan
            or DocumentScanStatus.ScanFailed
            || (document.ScanStatus == DocumentScanStatus.Scanning
                && document.LastScanAtUtc < DateTime.UtcNow.AddMinutes(-10));
        if (!canRetry)
        {
            return ScanRetryConflict(document.ScanStatus);
        }

        var claimedAt = DateTime.UtcNow;
        var claim = database.Documents
            .Where(item => item.Id == id
                && item.OwnerId == ownerId
                && !item.IsDeleted
                && item.ScanStatus == document.ScanStatus);
        if (document.ScanStatus == DocumentScanStatus.Scanning)
        {
            var staleBefore = claimedAt.AddMinutes(-10);
            claim = claim.Where(item => item.LastScanAtUtc < staleBefore);
        }

        var claimed = await claim.ExecuteUpdateAsync(
            setters => setters
                .SetProperty(item => item.ScanStatus, DocumentScanStatus.Scanning)
                .SetProperty(item => item.ScanAttemptCount, item => item.ScanAttemptCount + 1)
                .SetProperty(item => item.LastScanAtUtc, claimedAt),
            cancellationToken);
        if (claimed == 0)
        {
            return ScanRetryConflict(document.ScanStatus);
        }

        DocumentScanStatus scanStatus;
        try
        {
            scanStatus = await ScanStoredDocumentAsync(
                id,
                document.StorageKey,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await database.Documents
                .Where(item => item.Id == id
                    && !item.IsDeleted
                    && item.ScanStatus == DocumentScanStatus.Scanning)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(item => item.ScanStatus, DocumentScanStatus.ScanFailed)
                        .SetProperty(item => item.LastScanAtUtc, DateTime.UtcNow),
                    CancellationToken.None);
            throw;
        }

        var completedAt = DateTime.UtcNow;
        var completed = await database.Documents
            .Where(item => item.Id == id
                && !item.IsDeleted
                && item.ScanStatus == DocumentScanStatus.Scanning)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.ScanStatus, scanStatus)
                    .SetProperty(item => item.LastScanAtUtc, completedAt),
                cancellationToken);
        if (completed == 0)
        {
            return NotFound();
        }
        await auditWriter.WriteAsync(
            HttpContext,
            id,
            "document.scan.retry",
            scanStatus == DocumentScanStatus.Clean ? AuditResult.Success : AuditResult.Denied,
            JsonSerializer.Serialize(new
            {
                scanStatus = ScanStatusName(scanStatus),
                scanAttemptCount = document.ScanAttemptCount + 1
            }),
            cancellationToken);

        return Ok(new DocumentScanStatusResponse(
            ScanStatusName(scanStatus),
            document.ScanAttemptCount + 1,
            completedAt));
    }

    [HttpPost("{id:guid}/versions/{versionNumber:int}/scan/retry")]
    [ProducesResponseType<DocumentScanStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DocumentScanStatusResponse>> RetryVersionScan(
        Guid id,
        int versionNumber,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var ownerId))
        {
            return Unauthorized();
        }

        var document = await database.Documents
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.Id == id && item.OwnerId == ownerId && !item.IsDeleted,
                cancellationToken);
        var version = await database.DocumentVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.DocumentId == id && item.VersionNumber == versionNumber,
                cancellationToken);
        if (document is null || version is null)
        {
            return NotFound();
        }

        var canRetry = version.ScanStatus is DocumentScanStatus.PendingScan
            or DocumentScanStatus.ScanFailed
            || (version.ScanStatus == DocumentScanStatus.Scanning
                && version.LastScanAtUtc < DateTime.UtcNow.AddMinutes(-10));
        if (!canRetry)
        {
            return ScanRetryConflict(version.ScanStatus);
        }

        var claimedAt = DateTime.UtcNow;
        var claim = database.DocumentVersions
            .Where(item => item.Id == version.Id
                && item.ScanStatus == version.ScanStatus);
        if (version.ScanStatus == DocumentScanStatus.Scanning)
        {
            var staleBefore = claimedAt.AddMinutes(-10);
            claim = claim.Where(item => item.LastScanAtUtc < staleBefore);
        }

        var claimed = await claim.ExecuteUpdateAsync(
            setters => setters
                .SetProperty(item => item.ScanStatus, DocumentScanStatus.Scanning)
                .SetProperty(item => item.ScanAttemptCount, item => item.ScanAttemptCount + 1)
                .SetProperty(item => item.LastScanAtUtc, claimedAt),
            cancellationToken);
        if (claimed == 0)
        {
            return ScanRetryConflict(version.ScanStatus);
        }

        DocumentScanStatus scanStatus;
        try
        {
            scanStatus = await ScanStoredDocumentAsync(
                id,
                version.StorageKey,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await database.DocumentVersions
                .Where(item => item.Id == version.Id
                    && item.ScanStatus == DocumentScanStatus.Scanning)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(item => item.ScanStatus, DocumentScanStatus.ScanFailed)
                        .SetProperty(item => item.LastScanAtUtc, DateTime.UtcNow),
                    CancellationToken.None);
            throw;
        }

        var completedAt = DateTime.UtcNow;
        var completed = await database.DocumentVersions
            .Where(item => item.Id == version.Id
                && item.ScanStatus == DocumentScanStatus.Scanning)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.ScanStatus, scanStatus)
                    .SetProperty(item => item.LastScanAtUtc, completedAt),
                cancellationToken);
        if (completed == 0)
        {
            return NotFound();
        }
        await auditWriter.WriteAsync(
            HttpContext,
            id,
            "document.scan.version.retry",
            scanStatus == DocumentScanStatus.Clean ? AuditResult.Success : AuditResult.Denied,
            JsonSerializer.Serialize(new
            {
                versionNumber,
                scanStatus = ScanStatusName(scanStatus),
                scanAttemptCount = version.ScanAttemptCount + 1
            }),
            cancellationToken);

        return Ok(new DocumentScanStatusResponse(
            ScanStatusName(scanStatus),
            version.ScanAttemptCount + 1,
            completedAt));
    }

    [HttpGet("{id:guid}/versions/{versionNumber:int}/content")]
    public async Task<IActionResult> DownloadVersion(
        Guid id,
        int versionNumber,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        if ((await FindReadableDocumentAsync(id, userId, cancellationToken)).Document is null)
        {
            await RecordDeniedAccessAsync(id, "document.version.download", cancellationToken);
            return NotFound();
        }

        var version = await database.DocumentVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.DocumentId == id && item.VersionNumber == versionNumber,
                cancellationToken);
        if (version is null)
        {
            return NotFound();
        }
        if (version.ScanStatus != DocumentScanStatus.Clean)
        {
            return await ScanBlockedAsync(
                id,
                "document.version.download",
                version.ScanStatus,
                cancellationToken);
        }

        Stream content;
        try
        {
            content = await storage.OpenReadAsync(version.StorageKey, cancellationToken);
        }
        catch (FileNotFoundException exception)
        {
            logger.LogError(
                exception,
                "Stored document version {DocumentId} version {VersionNumber} is missing.",
                id,
                versionNumber);
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Document storage is temporarily unavailable.");
        }

        try
        {
            await auditWriter.WriteAsync(
                HttpContext,
                id,
                "document.version.download",
                AuditResult.Success,
                JsonSerializer.Serialize(new { versionNumber }),
                cancellationToken);
        }
        catch
        {
            await content.DisposeAsync();
            throw;
        }

        return File(content, version.ContentType, version.OriginalFileName);
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

    private Task RecordScanCandidateDeniedAsync(
        Guid documentId,
        string action,
        DocumentScanStatus scanStatus,
        CancellationToken cancellationToken) =>
        auditWriter.WriteAsync(
            HttpContext,
            documentId,
            action,
            AuditResult.Denied,
            JsonSerializer.Serialize(new { scanStatus = ScanStatusName(scanStatus) }),
            cancellationToken);

    private async Task<DocumentScanStatus> ScanStoredDocumentAsync(
        Guid documentId,
        string storageKey,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var content = await storage.OpenReadAsync(storageKey, cancellationToken);
            return await scanner.ScanAsync(content, cancellationToken) switch
            {
                DocumentScanVerdict.Clean => DocumentScanStatus.Clean,
                DocumentScanVerdict.Rejected => DocumentScanStatus.Rejected,
                _ => throw new InvalidOperationException("The scanner returned an unknown verdict.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or SocketException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Malware scan failed for document {DocumentId}.", documentId);
            return DocumentScanStatus.ScanFailed;
        }
    }

    private async Task<ObjectResult> ScanBlockedAsync(
        Guid documentId,
        string action,
        DocumentScanStatus scanStatus,
        CancellationToken cancellationToken)
    {
        await RecordDeniedAccessAsync(documentId, action, cancellationToken);
        return ScanBlocked(scanStatus);
    }

    private static ObjectResult ScanBlocked(DocumentScanStatus scanStatus)
    {
        var details = new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "Document content is unavailable until a malware scan completes successfully.",
            Detail = $"Current scan status: {ScanStatusName(scanStatus)}."
        };
        return new ObjectResult(details) { StatusCode = StatusCodes.Status409Conflict };
    }

    private static ObjectResult ScanRetryConflict(DocumentScanStatus scanStatus) =>
        new(new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "This document cannot be retried in its current scan state.",
            Detail = $"Current scan status: {ScanStatusName(scanStatus)}."
        })
        {
            StatusCode = StatusCodes.Status409Conflict
        };

    private static ObjectResult ScanCandidateFailure(DocumentScanStatus scanStatus) =>
        scanStatus == DocumentScanStatus.Rejected
            ? new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Title = "The uploaded content was rejected by the malware scanner."
            })
            {
                StatusCode = StatusCodes.Status422UnprocessableEntity
            }
            : new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "The malware scanner is unavailable. The content was not saved."
            })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable
            };

    private bool TryGetUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);

    private static bool IsTextEditableExtension(string extension) =>
        extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".doc", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".docx", StringComparison.OrdinalIgnoreCase);

    private static string ScanStatusName(DocumentScanStatus status) =>
        status switch
        {
            DocumentScanStatus.PendingScan => "PENDING_SCAN",
            DocumentScanStatus.Scanning => "SCANNING",
            DocumentScanStatus.Clean => "CLEAN",
            DocumentScanStatus.Rejected => "REJECTED",
            DocumentScanStatus.ScanFailed => "SCAN_FAILED",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
        };

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
            permission?.ToString().ToUpperInvariant(),
            ScanStatusName(document.ScanStatus),
            document.ScanAttemptCount,
            document.LastScanAtUtc);
}
