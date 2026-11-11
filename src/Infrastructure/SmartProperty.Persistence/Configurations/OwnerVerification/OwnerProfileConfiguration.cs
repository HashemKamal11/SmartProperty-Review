using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.OwnerVerification;

namespace SmartProperty.Persistence.Configurations.OwnerVerification;

internal sealed class OwnerProfileConfiguration : IEntityTypeConfiguration<OwnerProfile>
{
    /// <summary>One profile per user.</summary>
    public const string UserUniqueIndexName = "ux_identity_owner_profiles_user_id";

    /// <summary>
    /// One profile per national ID of a country. Rows that have not saved the ID step hold null and do not collide.
    /// </summary>
    public const string NationalIdUniqueIndexName = "ux_identity_owner_profiles_national_id";

    public void Configure(EntityTypeBuilder<OwnerProfile> builder)
    {
        builder.ToTable("owner_profiles", "identity", tableBuilder =>
        {
            tableBuilder.HasCheckConstraint(
                "ck_identity_owner_profiles_status",
                "status IN ('Draft', 'UnderReview', 'Approved', 'Rejected')");

            // The country and the ID are saved together. The shape per country is a backstop for the domain rules
            // (which are stricter: the Saudi first digit and the Egyptian birth date are checked only there).
            // The explicit IS NOT NULL tests matter: a CHECK whose result is NULL passes, so without them an ID with
            // no country (or a country with no ID) would be accepted.
            tableBuilder.HasCheckConstraint(
                "ck_identity_owner_profiles_national_id",
                "(national_id IS NULL AND id_country_code IS NULL)"
                + " OR (national_id IS NOT NULL AND id_country_code IS NOT NULL AND ("
                + "(id_country_code = 'SA' AND national_id ~ '^[0-9]{10}$')"
                + " OR (id_country_code = 'EG' AND national_id ~ '^[23][0-9]{13}$')"
                + " OR (id_country_code = 'AE' AND national_id ~ '^784[0-9]{12}$')))");

            tableBuilder.HasCheckConstraint(
                "ck_identity_owner_profiles_address_numbers",
                "(building_number IS NULL OR building_number ~ '^[0-9]{1,10}$')"
                + " AND (postal_code IS NULL OR postal_code ~ '^[0-9]{5}$')"
                + " AND (additional_number IS NULL OR additional_number ~ '^[0-9]{1,10}$')");

            // A decided profile names its reviewer and time; a rejected one carries its reason.
            tableBuilder.HasCheckConstraint(
                "ck_identity_owner_profiles_review",
                "(status IN ('Approved', 'Rejected')) = (reviewed_at IS NOT NULL AND reviewed_by_user_id IS NOT NULL)"
                + " AND ((status = 'Rejected') = (rejection_reason IS NOT NULL))");
        });

        builder.HasKey(profile => profile.Id);

        builder.Property(profile => profile.Id)
            .HasColumnName("id");

        builder.Property(profile => profile.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.Property(profile => profile.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(profile => profile.IdCountryCode)
            .HasColumnName("id_country_code")
            .HasMaxLength(NationalIdRules.CountryCodeLength);

        builder.Property(profile => profile.NationalId)
            .HasColumnName("national_id")
            .HasMaxLength(NationalIdRules.MaxNationalIdLength);

        builder.Property(profile => profile.Nationality)
            .HasColumnName("nationality")
            .HasMaxLength(OwnerProfile.MaxTextLength);

        builder.Property(profile => profile.BirthDate)
            .HasColumnName("birth_date");

        builder.Property(profile => profile.IdExpiryDate)
            .HasColumnName("id_expiry_date");

        builder.Property(profile => profile.IdentityConfirmedAt)
            .HasColumnName("identity_confirmed_at");

        builder.Property(profile => profile.BuildingNumber)
            .HasColumnName("building_number")
            .HasMaxLength(OwnerProfile.MaxBuildingNumberLength);

        builder.Property(profile => profile.StreetName)
            .HasColumnName("street_name")
            .HasMaxLength(OwnerProfile.MaxTextLength);

        builder.Property(profile => profile.District)
            .HasColumnName("district")
            .HasMaxLength(OwnerProfile.MaxTextLength);

        builder.Property(profile => profile.City)
            .HasColumnName("city")
            .HasMaxLength(OwnerProfile.MaxTextLength);

        builder.Property(profile => profile.PostalCode)
            .HasColumnName("postal_code")
            .HasMaxLength(OwnerProfile.PostalCodeLength);

        builder.Property(profile => profile.AdditionalNumber)
            .HasColumnName("additional_number")
            .HasMaxLength(OwnerProfile.MaxAdditionalNumberLength);

        builder.Property(profile => profile.AddressConfirmedAt)
            .HasColumnName("address_confirmed_at");

        builder.Property(profile => profile.AddressProofKey)
            .HasColumnName("address_proof_key")
            .HasMaxLength(300);

        builder.Property(profile => profile.AddressProofContentType)
            .HasColumnName("address_proof_content_type")
            .HasMaxLength(100);

        builder.Property(profile => profile.AddressProofFileName)
            .HasColumnName("address_proof_file_name")
            .HasMaxLength(OwnerProfile.MaxTextLength);

        builder.Property(profile => profile.SubmittedAt)
            .HasColumnName("submitted_at");

        builder.Property(profile => profile.ReviewedAt)
            .HasColumnName("reviewed_at");

        builder.Property(profile => profile.ReviewedByUserId)
            .HasColumnName("reviewed_by_user_id");

        builder.Property(profile => profile.RejectionReason)
            .HasColumnName("rejection_reason")
            .HasMaxLength(OwnerProfile.MaxRejectionReasonLength);

        builder.Property(profile => profile.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(profile => profile.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Users are deactivated, never deleted, so nothing cascades.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(profile => profile.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(profile => profile.ReviewedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(profile => profile.UserId)
            .IsUnique()
            .HasDatabaseName(UserUniqueIndexName);

        builder.HasIndex(profile => new { profile.IdCountryCode, profile.NationalId })
            .IsUnique()
            .HasDatabaseName(NationalIdUniqueIndexName)
            .HasFilter("national_id IS NOT NULL");

        // The reviewers' queue filters by status and reads in submission order.
        builder.HasIndex(profile => new { profile.Status, profile.SubmittedAt });
    }
}
