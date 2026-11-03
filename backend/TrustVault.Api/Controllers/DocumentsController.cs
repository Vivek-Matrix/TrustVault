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
            .Where(document => document.OwnerId == ownerId && !document.IsDeleted)
            .OrderByDescending(document => document.CreatedAtUtc)
            .Select(document => new DocumentResponse(
                document.Id,
                document.Title,
                document.Description,
                document.Category,
                document.OriginalFileName,
                document.SizeBytes,
                document.CreatedAtUtc,
                document.UpdatedAtUtc))
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

        var document = await database.Documents
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.Id == id
                    && item.OwnerId == ownerId
                    && !item.IsDeleted,
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
        return Ok(ToResponse(document));
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

        var document = await database.Documents
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.Id == id
                    && item.OwnerId == ownerId
                    && !item.IsDeleted,
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

    private static DocumentResponse ToResponse(Document document) =>
        new(
            document.Id,
            document.Title,
            document.Description,
            document.Category,
            document.OriginalFileName,
            document.SizeBytes,
            document.CreatedAtUtc,
            document.UpdatedAtUtc);
}
