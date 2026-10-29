using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Persistence.Configurations.Identity;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public const string EmailUniqueIndexName = "ux_identity_users_email";
    public const string PhoneNumberUniqueIndexName = "ux_identity_users_phone_number";

    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users", "identity");

        builder.HasKey(user => user.Id);

        builder.Property(user => user.Id)
            .HasColumnName("id");

        builder.Property(user => user.Email)
            .HasColumnName("email")
            .HasMaxLength(320)
            .IsRequired();

        builder.HasIndex(user => user.Email)
            .IsUnique()
            .HasDatabaseName(EmailUniqueIndexName);

        // Nullable only for accounts that existed before phone numbers were collected. PostgreSQL treats NULLs as
        // distinct in a unique index, so those accounts do not collide with each other.
        builder.Property(user => user.PhoneNumber)
            .HasColumnName("phone_number")
            .HasMaxLength(PhoneNumberFormat.MaxLength);

        builder.HasIndex(user => user.PhoneNumber)
            .IsUnique()
            .HasDatabaseName(PhoneNumberUniqueIndexName);

        builder.Property(user => user.PhoneVerifiedAt)
            .HasColumnName("phone_verified_at");

        builder.Ignore(user => user.IsPhoneVerified);

        builder.Property(user => user.FirstName)
            .HasColumnName("first_name")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(user => user.LastName)
            .HasColumnName("last_name")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(user => user.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(user => user.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(user => user.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();
    }
}
