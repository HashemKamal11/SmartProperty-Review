using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.Locations;

namespace SmartProperty.Persistence.Configurations.Locations;

/// <summary>The names of the unique indexes that keep one row per AI service id within a level.</summary>
internal static class LocationIndexNames
{
    public const string Country = "ux_locations_countries_ai_id";
    public const string City = "ux_locations_cities_ai_id";
    public const string Area = "ux_locations_areas_ai_id";
    public const string Compound = "ux_locations_compounds_ai_id";
}

internal sealed class CountryConfiguration : IEntityTypeConfiguration<Country>
{
    public void Configure(EntityTypeBuilder<Country> builder)
    {
        builder.ToTable("countries", "locations");
        builder.HasKey(country => country.Id);

        builder.Property(country => country.Id).HasColumnName("id");
        builder.Property(country => country.AiId).HasColumnName("ai_id").IsRequired();
        builder.Property(country => country.NameEn).HasColumnName("name_en").HasMaxLength(200).IsRequired();
        builder.Property(country => country.NameAr).HasColumnName("name_ar").HasMaxLength(200).IsRequired();

        builder.HasIndex(country => country.AiId).IsUnique().HasDatabaseName(LocationIndexNames.Country);
    }
}

internal sealed class CityConfiguration : IEntityTypeConfiguration<City>
{
    public void Configure(EntityTypeBuilder<City> builder)
    {
        builder.ToTable("cities", "locations");
        builder.HasKey(city => city.Id);

        builder.Property(city => city.Id).HasColumnName("id");
        builder.Property(city => city.AiId).HasColumnName("ai_id").IsRequired();
        builder.Property(city => city.CountryId).HasColumnName("country_id").IsRequired();
        builder.Property(city => city.NameEn).HasColumnName("name_en").HasMaxLength(200).IsRequired();
        builder.Property(city => city.NameAr).HasColumnName("name_ar").HasMaxLength(200).IsRequired();

        builder.HasOne(city => city.Country)
            .WithMany()
            .HasForeignKey(city => city.CountryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(city => city.AiId).IsUnique().HasDatabaseName(LocationIndexNames.City);
        builder.HasIndex(city => city.CountryId);
    }
}

internal sealed class AreaConfiguration : IEntityTypeConfiguration<Area>
{
    public void Configure(EntityTypeBuilder<Area> builder)
    {
        builder.ToTable("areas", "locations");
        builder.HasKey(area => area.Id);

        builder.Property(area => area.Id).HasColumnName("id");
        builder.Property(area => area.AiId).HasColumnName("ai_id").IsRequired();
        builder.Property(area => area.CityId).HasColumnName("city_id").IsRequired();
        builder.Property(area => area.NameEn).HasColumnName("name_en").HasMaxLength(200).IsRequired();
        builder.Property(area => area.NameAr).HasColumnName("name_ar").HasMaxLength(200).IsRequired();

        builder.HasOne(area => area.City)
            .WithMany()
            .HasForeignKey(area => area.CityId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(area => area.AiId).IsUnique().HasDatabaseName(LocationIndexNames.Area);
        builder.HasIndex(area => area.CityId);
    }
}

internal sealed class CompoundConfiguration : IEntityTypeConfiguration<Compound>
{
    public void Configure(EntityTypeBuilder<Compound> builder)
    {
        builder.ToTable("compounds", "locations");
        builder.HasKey(compound => compound.Id);

        builder.Property(compound => compound.Id).HasColumnName("id");
        builder.Property(compound => compound.AiId).HasColumnName("ai_id").IsRequired();
        builder.Property(compound => compound.AreaId).HasColumnName("area_id").IsRequired();
        builder.Property(compound => compound.NameEn).HasColumnName("name_en").HasMaxLength(200).IsRequired();
        builder.Property(compound => compound.NameAr).HasColumnName("name_ar").HasMaxLength(200).IsRequired();

        builder.HasOne(compound => compound.Area)
            .WithMany()
            .HasForeignKey(compound => compound.AreaId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(compound => compound.AiId).IsUnique().HasDatabaseName(LocationIndexNames.Compound);
        builder.HasIndex(compound => compound.AreaId);
    }
}
