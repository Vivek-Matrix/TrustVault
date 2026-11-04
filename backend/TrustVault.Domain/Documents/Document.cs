namespace TrustVault.Domain.Documents;

public sealed class Document
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Category { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string StorageKey { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public bool IsDeleted { get; set; }
    public DocumentScanStatus ScanStatus { get; set; } = DocumentScanStatus.PendingScan;
    public int ScanAttemptCount { get; set; }
    public DateTime? LastScanAtUtc { get; set; }
    public ICollection<DocumentGrant> Grants { get; set; } = new List<DocumentGrant>();
    public ICollection<DocumentVersion> Versions { get; set; } = new List<DocumentVersion>();
}
