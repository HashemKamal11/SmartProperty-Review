using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Persistence.Configurations.Identity;

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("roles", "identity", tableBuilder => tableBuilder.HasCheckConstraint(
            "ck_identity_roles_scope_workspace",
            "(scope = 'Platform' AND workspace_id IS NULL) OR (scope = 'Workspace' AND workspace_id IS NOT NULL)"));

        builder.HasKey(role => role.Id);

        builder.Property(role => role.Id)
            .HasColumnName("id");

        builder.Property(role => role.Name)
            .HasColumnName("name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(role => role.Scope)
            .HasColumnName("scope")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(role => role.WorkspaceId)
            .HasColumnName("workspace_id");

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(role => role.WorkspaceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(role => role.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(role => role.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasIndex(role => role.WorkspaceId)
            .HasDatabaseName("ix_identity_roles_workspace_id");
    }
}
