using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Persistence.Configurations.Platform;

internal sealed class WorkspaceConfiguration : IEntityTypeConfiguration<Workspace>
{
    public void Configure(EntityTypeBuilder<Workspace> builder)
    {
        builder.ToTable("workspaces", "platform");

        builder.HasKey(workspace => workspace.Id);

        builder.Property(workspace => workspace.Id)
            .HasColumnName("id");

        builder.Property(workspace => workspace.Name)
            .HasColumnName("name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(workspace => workspace.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(workspace => workspace.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();
    }
}
