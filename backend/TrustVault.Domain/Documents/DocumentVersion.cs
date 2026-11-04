namespace TrustVault.Domain.Documents;

public sealed class DocumentVersion
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public int VersionNumber { get; set; }
    public string StorageKey { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public Guid UploadedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DocumentScanStatus ScanStatus { get; set; } = DocumentScanStatus.PendingScan;
    public int ScanAttemptCount { get; set; }
    public DateTime? LastScanAtUtc { get; set; }
}
