namespace TrustVault.Api.Models.Admin;

/// <summary>Platform-level counts that do not include document contents.</summary>
public sealed record AdminDashboardResponse(
    int ActiveMembers,
    int SuspendedMembers,
    int ActiveDocuments,
    int DocumentsAwaitingScan,
    int ActiveGrants,
    int DeniedAttemptsLast24Hours,
    int SuccessfulDownloadsLast24Hours);

/// <summary>An audit event returned to administrators for incident investigation.</summary>
public sealed record AdminAuditEventResponse(
    Guid Id,
    Guid? ActorUserId,
    string? ActorEmail,
    string ActorRole,
    Guid? DocumentId,
    string Action,
    string Result,
    DateTimeOffset TimestampUtc,
    string RequestId,
    string? IpAddress,
    string? UserAgent);

/// <summary>A page of audit events matching the requested filters.</summary>
public sealed record AdminAuditPageResponse(
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<AdminAuditEventResponse> Items);

/// <summary>A security signal derived from recorded audit events.</summary>
public sealed record AdminSecurityAlertResponse(
    string Type,
    string Severity,
    Guid? ActorUserId,
    string? ActorEmail,
    string? IpAddress,
    int EventCount,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc);

/// <summary>Document metadata visible to administrators without content access.</summary>
public sealed record AdminDocumentResponse(
    Guid Id,
    string Title,
    string Category,
    string OriginalFileName,
    long SizeBytes,
    Guid OwnerId,
    string OwnerEmail,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int VersionCount,
    int ActiveGrantCount,
    string ScanStatus,
    bool IsDeleted);

/// <summary>A page of document metadata matching administrator filters.</summary>
public sealed record AdminDocumentPageResponse(
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<AdminDocumentResponse> Items);

/// <summary>One current or historical document permission grant.</summary>
public sealed record AdminPermissionResponse(
    Guid UserId,
    string Email,
    string Permission,
    Guid GrantedByUserId,
    DateTimeOffset GrantedAtUtc,
    DateTimeOffset? RevokedAtUtc,
    string? RevocationReason);

/// <summary>An active named-user share visible to administrators.</summary>
public sealed record AdminShareResponse(
    Guid DocumentId,
    string DocumentTitle,
    Guid OwnerId,
    string OwnerEmail,
    Guid UserId,
    string UserEmail,
    string Permission,
    DateTimeOffset GrantedAtUtc);

/// <summary>A page of active named-user shares.</summary>
public sealed record AdminSharePageResponse(
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<AdminShareResponse> Items);

/// <summary>Request to revoke a document permission as an administrator.</summary>
public sealed record AdminRevokePermissionRequest
{
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.StringLength(500, MinimumLength = 8)]
    public required string Reason { get; init; }
}

/// <summary>Request to reset a member's credentials with an auditable reason.</summary>
public sealed record AdminResetCredentialsRequest
{
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.StringLength(500, MinimumLength = 8)]
    public required string Reason { get; init; }
}

/// <summary>Request to reassign document ownership during account offboarding.</summary>
public sealed record AdminReassignDocumentRequest
{
    [System.ComponentModel.DataAnnotations.Required]
    public Guid NewOwnerId { get; init; }

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.StringLength(500, MinimumLength = 8)]
    public required string Reason { get; init; }
}

/// <summary>A one-time credential reset response.</summary>
public sealed record AdminResetCredentialsResponse(
    Guid UserId,
    string Email,
    string TemporaryPassword,
    bool PasswordChangeRequired);

/// <summary>Read-only operational policy currently enforced by the application.</summary>
public sealed record AdminSystemSettingsResponse(
    IReadOnlyList<string> AllowedDocumentExtensions,
    long MaximumUploadBytes,
    int AccessTokenLifetimeMinutes,
    int MaximumFailedSignIns,
    int LockoutMinutes,
    bool ExternalGuestLinksEnabled,
    string ConfigurationManagement);
