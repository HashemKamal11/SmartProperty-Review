using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Persistence.Configurations.Identity;

internal sealed class UserPlatformRoleConfiguration : IEntityTypeConfiguration<UserPlatformRole>
{
    public void Configure(EntityTypeBuilder<UserPlatformRole> builder)
    {
        builder.ToTable("user_platform_roles", "identity");

        builder.HasKey(userPlatformRole => new
        {
            userPlatformRole.UserId,
            userPlatformRole.RoleId
        });

        builder.Property(userPlatformRole => userPlatformRole.UserId)
            .HasColumnName("user_id");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(userPlatformRole => userPlatformRole.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(userPlatformRole => userPlatformRole.RoleId)
            .HasColumnName("role_id");

        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(userPlatformRole => userPlatformRole.RoleId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
