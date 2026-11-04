using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TrustVault.Domain.Audit;
using TrustVault.Domain.Documents;
using TrustVault.Infrastructure.Persistence.Entities;

namespace TrustVault.Infrastructure.Persistence;

public sealed class ApplicationDbContext(
    DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentGrant> DocumentGrants => Set<DocumentGrant>();
    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureAuditEventsAreAppendOnly();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        EnsureAuditEventsAreAppendOnly();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(user => user.CreatedAtUtc).HasColumnType("timestamp with time zone");
            entity.Property(user => user.RefreshTokenHash).HasMaxLength(128);
            entity.HasIndex(user => user.IsSuspended);
            entity.HasIndex(user => user.IsPrimaryAdministrator)
                .IsUnique()
                .HasFilter("\"IsPrimaryAdministrator\" = TRUE");
        });

        builder.Entity<Document>(entity =>
        {
            entity.ToTable("documents");
            entity.HasKey(document => document.Id);
            entity.Property(document => document.Title).HasMaxLength(200).IsRequired();
            entity.Property(document => document.Description).HasMaxLength(2000);
            entity.Property(document => document.Category).HasMaxLength(100).IsRequired();
            entity.Property(document => document.OriginalFileName).HasMaxLength(255).IsRequired();
            entity.Property(document => document.ContentType).HasMaxLength(127).IsRequired();
            entity.Property(document => document.StorageKey).HasMaxLength(512).IsRequired();
            entity.Property(document => document.ScanStatus)
                .HasConversion(
                    status => status.ToString().ToUpperInvariant(),
                    status => Enum.Parse<DocumentScanStatus>(status, true))
                .HasMaxLength(16)
                .HasDefaultValue(DocumentScanStatus.PendingScan);
            entity.HasIndex(document => document.StorageKey).IsUnique();
            entity.HasIndex(document => new { document.OwnerId, document.CreatedAtUtc });
            entity.HasIndex(document => new { document.Category, document.CreatedAtUtc });
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(document => document.OwnerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<DocumentGrant>(entity =>
        {
            entity.ToTable("document_grants");
            entity.HasKey(grant => grant.Id);
            entity.Property(grant => grant.Permission)
                .HasConversion(
                    permission => permission.ToString().ToUpperInvariant(),
                    permission => Enum.Parse<DocumentPermission>(permission, true))
                .HasMaxLength(16);
            entity.Property(grant => grant.RevocationReason).HasMaxLength(500);
            entity.HasIndex(grant => new { grant.UserId, grant.DocumentId });
            entity.HasIndex(grant => new { grant.DocumentId, grant.UserId })
                .IsUnique()
                .HasFilter("\"RevokedAtUtc\" IS NULL");
            entity.HasOne<Document>()
                .WithMany(document => document.Grants)
                .HasForeignKey(grant => grant.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(grant => grant.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(grant => grant.GrantedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<DocumentVersion>(entity =>
        {
            entity.ToTable("document_versions");
            entity.HasKey(version => version.Id);
            entity.Property(version => version.StorageKey).HasMaxLength(512).IsRequired();
            entity.Property(version => version.OriginalFileName).HasMaxLength(255).IsRequired();
            entity.Property(version => version.ContentType).HasMaxLength(127).IsRequired();
            entity.Property(version => version.ScanStatus)
                .HasConversion(
                    status => status.ToString().ToUpperInvariant(),
                    status => Enum.Parse<DocumentScanStatus>(status, true))
                .HasMaxLength(16)
                .HasDefaultValue(DocumentScanStatus.PendingScan);
            entity.HasIndex(version => new { version.DocumentId, version.VersionNumber }).IsUnique();
            entity.HasOne<Document>()
                .WithMany(document => document.Versions)
                .HasForeignKey(version => version.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(version => version.UploadedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<AuditEvent>(entity =>
        {
            entity.ToTable("audit_events");
            entity.HasKey(auditEvent => auditEvent.Id);
            entity.Property(auditEvent => auditEvent.ActorRole).HasMaxLength(64).IsRequired();
            entity.Property(auditEvent => auditEvent.Action).HasMaxLength(100).IsRequired();
            entity.Property(auditEvent => auditEvent.Result)
                .HasConversion(
                    result => result.ToString().ToUpperInvariant(),
                    result => Enum.Parse<AuditResult>(result, true))
                .HasMaxLength(16);
            entity.Property(auditEvent => auditEvent.RequestId).HasMaxLength(128).IsRequired();
            entity.Property(auditEvent => auditEvent.IpAddress).HasMaxLength(45);
            entity.Property(auditEvent => auditEvent.UserAgent).HasMaxLength(512);
            entity.Property(auditEvent => auditEvent.MetadataJson).HasColumnType("jsonb").IsRequired();
            entity.Property(auditEvent => auditEvent.PreviousHash).HasMaxLength(64);
            entity.Property(auditEvent => auditEvent.RecordHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(auditEvent => new { auditEvent.ActorUserId, auditEvent.TimestampUtc });
            entity.HasIndex(auditEvent => new { auditEvent.DocumentId, auditEvent.TimestampUtc });
            entity.HasIndex(auditEvent => auditEvent.TimestampUtc);
        });
    }

    private void EnsureAuditEventsAreAppendOnly()
    {
        var mutation = ChangeTracker.Entries<AuditEvent>()
            .FirstOrDefault(entry => entry.State is EntityState.Modified or EntityState.Deleted);
        if (mutation is not null)
        {
            throw new InvalidOperationException("Audit events are append-only.");
        }
    }
}
