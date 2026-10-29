using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Persistence.Configurations.Identity;

internal sealed class PhoneVerificationConfiguration : IEntityTypeConfiguration<PhoneVerification>
{
    public void Configure(EntityTypeBuilder<PhoneVerification> builder)
    {
        builder.ToTable("phone_verifications", "identity", tableBuilder =>
        {
            tableBuilder.HasCheckConstraint(
                "ck_identity_phone_verifications_expires_after_created",
                "expires_at > created_at");

            tableBuilder.HasCheckConstraint(
                "ck_identity_phone_verifications_failed_attempts_not_negative",
                "failed_attempts >= 0");
        });

        builder.HasKey(verification => verification.Id);

        builder.Property(verification => verification.Id)
            .HasColumnName("id");

        builder.Property(verification => verification.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(verification => verification.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(verification => verification.CodeHash)
            .HasColumnName("code_hash")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(verification => verification.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(verification => verification.ExpiresAt)
            .HasColumnName("expires_at")
            .IsRequired();

        // Optimistic concurrency, so two parallel wrong guesses cannot both read the same count and overwrite each
        // other: the second UPDATE affects zero rows and fails instead of earning a free attempt.
        builder.Property(verification => verification.FailedAttempts)
            .HasColumnName("failed_attempts")
            .HasDefaultValue(0)
            .IsConcurrencyToken();

        builder.Property(verification => verification.ConsumedAt)
            .HasColumnName("consumed_at");

        builder.HasIndex(verification => new { verification.UserId, verification.CreatedAt })
            .HasDatabaseName("ix_identity_phone_verifications_user_id_created_at");
    }
}
