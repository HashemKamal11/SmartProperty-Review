using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Persistence.Configurations.Identity;

internal sealed class WorkspaceRegistrationRequestConfiguration : IEntityTypeConfiguration<WorkspaceRegistrationRequest>
{
    /// <summary>At most one Pending request per applicant; the index the application relies on under concurrency.</summary>
    public const string PendingApplicantUniqueIndexName = "ux_identity_workspace_registration_requests_pending_applicant";

    public void Configure(EntityTypeBuilder<WorkspaceRegistrationRequest> builder)
    {
        builder.ToTable("workspace_registration_requests", "identity");

        builder.HasKey(request => request.Id);

        builder.Property(request => request.Id).HasColumnName("id");

        builder.Property(request => request.ApplicantUserId)
            .HasColumnName("applicant_user_id")
            .IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(request => request.ApplicantUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(request => request.CompanyName)
            .HasColumnName("company_name")
            .HasMaxLength(WorkspaceRegistrationRequest.CompanyNameMaxLength)
            .IsRequired();

        builder.Property(request => request.CommercialRegistrationNumber)
            .HasColumnName("commercial_registration_number")
            .HasMaxLength(WorkspaceRegistrationRequest.CommercialRegistrationNumberMaxLength);

        // A concurrency token: of two reviewers deciding at once, only the first save matches the row.
        builder.Property(request => request.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired()
            .IsConcurrencyToken();

        builder.Property(request => request.RequestedAt)
            .HasColumnName("requested_at")
            .IsRequired();

        builder.Property(request => request.ReviewedAt).HasColumnName("reviewed_at");

        builder.Property(request => request.ReviewedByUserId).HasColumnName("reviewed_by_user_id");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(request => request.ReviewedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(request => request.RejectionReason)
            .HasColumnName("rejection_reason")
            .HasMaxLength(WorkspaceRegistrationRequest.RejectionReasonMaxLength);

        builder.Property(request => request.WorkspaceId).HasColumnName("workspace_id");

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(request => request.WorkspaceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(request => request.ApplicantUserId)
            .IsUnique()
            .HasFilter("status = 'Pending'")
            .HasDatabaseName(PendingApplicantUniqueIndexName);

        builder.HasIndex(request => new { request.Status, request.RequestedAt })
            .HasDatabaseName("ix_identity_workspace_registration_requests_status_requested_at");
    }
}
