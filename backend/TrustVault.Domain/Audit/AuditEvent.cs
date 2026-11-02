namespace TrustVault.Domain.Audit;

public sealed class AuditEvent
{
    public Guid Id { get; set; }
    public Guid? ActorUserId { get; set; }
    public string ActorRole { get; set; } = string.Empty;
    public Guid? DocumentId { get; set; }
    public string Action { get; set; } = string.Empty;
    public AuditResult Result { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public string? PreviousHash { get; set; }
    public string RecordHash { get; set; } = string.Empty;
}
