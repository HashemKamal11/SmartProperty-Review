using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Persistence.Configurations.Identity;

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens", "identity", tableBuilder =>
        {
            tableBuilder.HasCheckConstraint(
                "ck_identity_refresh_tokens_expires_after_created",
                "expires_at > created_at");

            tableBuilder.HasCheckConstraint(
                "ck_identity_refresh_tokens_revoked_after_created",
                "revoked_at IS NULL OR revoked_at >= created_at");
        });

        builder.HasKey(refreshToken => refreshToken.Id);

        builder.Property(refreshToken => refreshToken.Id)
            .HasColumnName("id");

        builder.Property(refreshToken => refreshToken.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(refreshToken => refreshToken.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(refreshToken => refreshToken.TokenHash)
            .HasColumnName("token_hash")
            .HasMaxLength(128)
            .IsRequired();

        builder.HasIndex(refreshToken => refreshToken.TokenHash)
            .IsUnique()
            .HasDatabaseName("ux_identity_refresh_tokens_token_hash");

        builder.Property(refreshToken => refreshToken.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(refreshToken => refreshToken.ExpiresAt)
            .HasColumnName("expires_at")
            .IsRequired();

        builder.Property(refreshToken => refreshToken.RevokedAt)
            .HasColumnName("revoked_at");

        builder.HasIndex(refreshToken => refreshToken.UserId)
            .HasDatabaseName("ix_identity_refresh_tokens_user_id");
    }
}
