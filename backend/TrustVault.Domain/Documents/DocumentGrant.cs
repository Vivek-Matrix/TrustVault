namespace TrustVault.Domain.Documents;

public sealed class DocumentGrant
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public Guid UserId { get; set; }
    public DocumentPermission Permission { get; set; }
    public Guid GrantedByUserId { get; set; }
    public DateTime GrantedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? RevocationReason { get; set; }
}
