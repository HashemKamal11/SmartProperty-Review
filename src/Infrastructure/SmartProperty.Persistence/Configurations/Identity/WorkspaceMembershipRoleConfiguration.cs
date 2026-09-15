using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Persistence.Configurations.Identity;

internal sealed class WorkspaceMembershipRoleConfiguration : IEntityTypeConfiguration<WorkspaceMembershipRole>
{
    public void Configure(EntityTypeBuilder<WorkspaceMembershipRole> builder)
    {
        builder.ToTable("workspace_membership_roles", "identity");

        builder.HasKey(workspaceMembershipRole => new
        {
            workspaceMembershipRole.WorkspaceMembershipId,
            workspaceMembershipRole.RoleId
        });

        builder.Property(workspaceMembershipRole => workspaceMembershipRole.WorkspaceMembershipId)
            .HasColumnName("workspace_membership_id");

        builder.HasOne<WorkspaceMembership>()
            .WithMany()
            .HasForeignKey(workspaceMembershipRole => workspaceMembershipRole.WorkspaceMembershipId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(workspaceMembershipRole => workspaceMembershipRole.RoleId)
            .HasColumnName("role_id");

        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(workspaceMembershipRole => workspaceMembershipRole.RoleId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
