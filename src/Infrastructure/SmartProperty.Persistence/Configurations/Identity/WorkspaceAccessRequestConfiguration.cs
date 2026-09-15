using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Persistence.Configurations.Identity;

internal sealed class WorkspaceAccessRequestConfiguration : IEntityTypeConfiguration<WorkspaceAccessRequest>
{
    public void Configure(EntityTypeBuilder<WorkspaceAccessRequest> builder)
    {
        builder.ToTable("workspace_access_requests", "identity");

        builder.HasKey(accessRequest => accessRequest.Id);

        builder.Property(accessRequest => accessRequest.Id)
            .HasColumnName("id");

        builder.Property(accessRequest => accessRequest.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(accessRequest => accessRequest.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(accessRequest => accessRequest.WorkspaceId)
            .HasColumnName("workspace_id")
            .IsRequired();

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(accessRequest => accessRequest.WorkspaceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(accessRequest => accessRequest.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(accessRequest => accessRequest.RequestedAt)
            .HasColumnName("requested_at")
            .IsRequired();

        builder.Property(accessRequest => accessRequest.ReviewedAt)
            .HasColumnName("reviewed_at");

        builder.Property(accessRequest => accessRequest.ReviewedByUserId)
            .HasColumnName("reviewed_by_user_id");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(accessRequest => accessRequest.ReviewedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(accessRequest => new
            {
                accessRequest.UserId,
                accessRequest.WorkspaceId,
                accessRequest.Status
            })
            .HasDatabaseName("ix_identity_workspace_access_requests_user_workspace_status");
    }
}
