using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Persistence.Configurations.Identity;

internal sealed class UserCredentialConfiguration : IEntityTypeConfiguration<UserCredential>
{
    public void Configure(EntityTypeBuilder<UserCredential> builder)
    {
        builder.ToTable("user_credentials", "identity", tableBuilder => tableBuilder.HasCheckConstraint(
            "ck_identity_user_credentials_updated_after_created",
            "updated_at >= created_at"));

        builder.HasKey(credential => credential.UserId);

        builder.Property(credential => credential.UserId)
            .HasColumnName("user_id")
            .ValueGeneratedNever();

        builder.HasOne<User>()
            .WithOne()
            .HasForeignKey<UserCredential>(credential => credential.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(credential => credential.PasswordHash)
            .HasColumnName("password_hash")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(credential => credential.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(credential => credential.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();
    }
}
