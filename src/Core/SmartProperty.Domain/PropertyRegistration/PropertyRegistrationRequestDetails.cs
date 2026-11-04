using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Domain.PropertyRegistration;

/// <summary>
/// Immutable canonical physical facts a registration request proposes for a property. It is shaped like a draft:
/// every member may be absent while the wizard is being filled in, and what is present must be valid. Completeness
/// is a separate question answered by <see cref="EnsureReadyForSubmission"/>.
/// </summary>
/// <remarks>
/// Listing and offering data (listing type, title, sale or rental prices, promotional description) is deliberately
/// absent: it belongs to the marketplace listing, not to the canonical property. Area carries no unit, and no
/// subtype exists, because neither has been approved. <see cref="PropertyAddress"/> exposes no setter outside its
/// own type, so a held address cannot be changed through this record.
/// </remarks>
public sealed record PropertyRegistrationRequestDetails
{
    private PropertyRegistrationRequestDetails()
    {
    }

    public PropertyRegistrationRequestDetails(
        PropertyType? type = null,
        PropertyAddress? address = null,
        decimal? area = null,
        int? bedrooms = null,
        int? bathrooms = null,
        int? constructionYear = null)
    {
        if (type is { } definedType && !Enum.IsDefined(definedType))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Property type is not supported.");
        }

        if (area is <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(area), area, "Area must be greater than zero.");
        }

        if (bedrooms is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bedrooms), bedrooms, "Bedrooms must not be negative.");
        }

        if (bathrooms is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bathrooms), bathrooms, "Bathrooms must not be negative.");
        }

        if (constructionYear is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(constructionYear),
                constructionYear,
                "Construction year must be greater than zero.");
        }

        // Rooms are meaningless for bare land; supplying them is a contradiction, not valid data to keep.
        if (type == PropertyType.Land && bedrooms is not null)
        {
            throw new ArgumentException("Bedrooms do not apply to land.", nameof(bedrooms));
        }

        if (type == PropertyType.Land && bathrooms is not null)
        {
            throw new ArgumentException("Bathrooms do not apply to land.", nameof(bathrooms));
        }

        Type = type;
        Address = address;
        Area = area;
        Bedrooms = bedrooms;
        Bathrooms = bathrooms;
        ConstructionYear = constructionYear;
    }

    public PropertyType? Type { get; private set; }
    public PropertyAddress? Address { get; private set; }
    public decimal? Area { get; private set; }
    public int? Bedrooms { get; private set; }
    public int? Bathrooms { get; private set; }
    public int? ConstructionYear { get; private set; }

    /// <summary>
    /// Names every requirement that is not yet met. Type, city, district, address line, and area are always
    /// required; a unit additionally requires bedrooms, bathrooms, and construction year. Land and building
    /// requirements beyond the common set have not been approved, so none are imposed.
    /// </summary>
    public IReadOnlyList<string> GetMissingRequirements()
    {
        var missing = new List<string>();

        if (Type is null)
        {
            missing.Add(nameof(Type));
        }

        if (Address is null)
        {
            missing.Add(nameof(Address));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(Address.City))
            {
                missing.Add($"{nameof(Address)}.{nameof(PropertyAddress.City)}");
            }

            if (string.IsNullOrWhiteSpace(Address.District))
            {
                missing.Add($"{nameof(Address)}.{nameof(PropertyAddress.District)}");
            }

            if (string.IsNullOrWhiteSpace(Address.AddressLine))
            {
                missing.Add($"{nameof(Address)}.{nameof(PropertyAddress.AddressLine)}");
            }
        }

        if (Area is null)
        {
            missing.Add(nameof(Area));
        }

        if (Type == PropertyType.Unit)
        {
            if (Bedrooms is null)
            {
                missing.Add(nameof(Bedrooms));
            }

            if (Bathrooms is null)
            {
                missing.Add(nameof(Bathrooms));
            }

            if (ConstructionYear is null)
            {
                missing.Add(nameof(ConstructionYear));
            }
        }

        return missing;
    }

    public void EnsureReadyForSubmission()
    {
        var missing = GetMissingRequirements();

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Registration details are incomplete. Missing: {string.Join(", ", missing)}.");
        }
    }
}
