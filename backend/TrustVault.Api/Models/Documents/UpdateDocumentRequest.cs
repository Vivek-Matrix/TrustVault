using System.ComponentModel.DataAnnotations;

namespace TrustVault.Api.Models.Documents;

public sealed class UpdateDocumentRequest
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Title { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; set; }

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string Category { get; set; } = string.Empty;
}
