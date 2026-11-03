using System.ComponentModel.DataAnnotations;

namespace TrustVault.Api.Models.Documents;

public sealed class GrantDocumentAccessRequest
{
    [Required]
    [EmailAddress]
    [StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [RegularExpression("^(READ|EDIT)$", ErrorMessage = "Permission must be READ or EDIT.")]
    public string Permission { get; set; } = string.Empty;
}
