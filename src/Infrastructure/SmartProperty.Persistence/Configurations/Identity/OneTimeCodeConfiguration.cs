using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Persistence.Configurations.Identity;

internal sealed class OneTimeCodeConfiguration : IEntityTypeConfiguration<OneTimeCode>
{
    public void Configure(EntityTypeBuilder<OneTimeCode> builder)
    {
        builder.ToTable("one_time_codes", "identity", tableBuilder =>
        {
            tableBuilder.HasCheckConstraint(
                "ck_identity_one_time_codes_expires_after_created",
                "expires_at > created_at");

            tableBuilder.HasCheckConstraint(
                "ck_identity_one_time_codes_failed_attempts_not_negative",
                "failed_attempts >= 0");
        });

        builder.HasKey(code => code.Id);

        builder.Property(code => code.Id)
            .HasColumnName("id");

        builder.Property(code => code.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(code => code.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(code => code.Purpose)
            .HasColumnName("purpose")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(code => code.CodeHash)
            .HasColumnName("code_hash")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(code => code.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(code => code.ExpiresAt)
            .HasColumnName("expires_at")
            .IsRequired();

        // Optimistic concurrency, so two parallel wrong guesses cannot both read the same count and overwrite each
        // other: the second UPDATE affects zero rows and fails instead of earning a free attempt.
        builder.Property(code => code.FailedAttempts)
            .HasColumnName("failed_attempts")
            .HasDefaultValue(0)
            .IsConcurrencyToken();

        builder.Property(code => code.ConsumedAt)
            .HasColumnName("consumed_at");

        builder.HasIndex(code => new { code.UserId, code.Purpose, code.CreatedAt })
            .HasDatabaseName("ix_identity_one_time_codes_user_id_purpose_created_at");
    }
}
