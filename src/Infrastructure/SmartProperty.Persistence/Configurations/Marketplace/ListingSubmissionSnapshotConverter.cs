using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Persistence.Configurations.Marketplace;

// Private persistence format, never an API contract. No payload is included in diagnostics.
internal static class ListingSubmissionSnapshotConverter
{
    internal static void Configure<T>(PropertyBuilder<T> property, string column) where T : class
    {
        property.HasColumnName(column).HasColumnType("jsonb")
            .HasConversion(value => Write(value), json => Read<T>(json));
        property.Metadata.SetValueComparer(new ValueComparer<T>(
            (a, b) => Write(a!) == Write(b!), value => Write(value).GetHashCode(), value => Read<T>(Write(value))));
    }

    internal static string Write<T>(T value) where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        object payload = value switch
        {
            ListingSubmissionContent c => ContentPayload(c),
            ListingLegalAuthority a => new { a.PropertyId, Purpose = (int)a.Purpose, DeclaredOwner = OwnerData(a.DeclaredOwner),
                a.ActingUserId, Representation = (int)a.Representation, a.WorkspaceId, a.ValidFrom, a.ValidUntil },
            ListingReviewContext r => new { r.ReviewerUserId, r.SubmitterUserId, DeclaredOwner = OwnerData(r.DeclaredOwner),
                r.SubmittingWorkspaceId, r.IdentitiesVerified, r.PlatformReviewAuthorized, r.ReviewerAffiliatedWithWorkspace, r.ReviewerHasOwnerConflict },
            IReadOnlyList<ListingEvidenceReference> e => e.Select(EvidenceData).ToArray(),
            IReadOnlyList<ListingEvidenceAssessment> a => a.Select(x => new { x.AssessmentId, Evidence = EvidenceData(x.Evidence), Status = (int)x.Status, x.AssessedAt }).ToArray(),
            _ => throw new InvalidOperationException("Unsupported private snapshot type.")
        };
        return JsonSerializer.Serialize(new { FormatVersion = 1, Payload = payload });
    }

    internal static T Read<T>(string json) where T : class
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("FormatVersion").GetInt32() != 1) throw new InvalidOperationException("Unsupported snapshot version.");
        ValidateUniqueMembers(root);
        var p = root.GetProperty("Payload");
        object value;
        if (typeof(T) == typeof(ListingSubmissionContent))
            value = ReadContent(p);
        else if (typeof(T) == typeof(ListingLegalAuthority))
            value = new ListingLegalAuthority(Id(p,"PropertyId"),(ListingPurpose)p.GetProperty("Purpose").GetInt32(),Owner(p.GetProperty("DeclaredOwner")),
                Id(p,"ActingUserId"),(ListingRepresentationKind)p.GetProperty("Representation").GetInt32(),OptionalId(p,"WorkspaceId"),
                p.GetProperty("ValidFrom").GetDateTimeOffset(),OptionalTime(p,"ValidUntil"));
        else if (typeof(T) == typeof(ListingReviewContext))
            value = new ListingReviewContext(Id(p,"ReviewerUserId"),Id(p,"SubmitterUserId"),Owner(p.GetProperty("DeclaredOwner")),OptionalId(p,"SubmittingWorkspaceId"),
                Flag(p,"IdentitiesVerified"),Flag(p,"PlatformReviewAuthorized"),Flag(p,"ReviewerAffiliatedWithWorkspace"),Flag(p,"ReviewerHasOwnerConflict"));
        else if (typeof(T) == typeof(IReadOnlyList<ListingEvidenceReference>))
            value = Array.AsReadOnly(p.EnumerateArray().Select(Evidence).ToArray());
        else if (typeof(T) == typeof(IReadOnlyList<ListingEvidenceAssessment>))
            value = Array.AsReadOnly(p.EnumerateArray().Select(x => new ListingEvidenceAssessment(Id(x,"AssessmentId"),Evidence(x.GetProperty("Evidence")),
                (EvidenceScanStatus)x.GetProperty("Status").GetInt32(),x.GetProperty("AssessedAt").GetDateTimeOffset())).ToArray());
        else throw new InvalidOperationException("Unsupported private snapshot type.");
        return (T)value;
    }


    // The plain members are written as always. Pricing options and rental terms are additive: they are written only
    // when present, so a Sale and a rental stored with one price keep exactly the snapshot they always had.
    private static object ContentPayload(ListingSubmissionContent c)
    {
        if (c.PricingOptions.Count == 0 && c.RentalTerms is null)
        {
            // A sale's negotiable flag and availability date are equally additive: written only when the owner stated
            // them. Insertion order is the member order, so the plain members keep exactly their old position.
            var payload = new Dictionary<string, object?>
            {
                ["Country"] = c.Country, ["Region"] = c.Region, ["City"] = c.City, ["District"] = c.District,
                ["Title"] = c.Title, ["Description"] = c.Description,
                ["PropertyType"] = (int)c.PropertyType, ["Purpose"] = (int)c.Purpose,
                ["Amount"] = c.Amount, ["Currency"] = c.Currency, ["RentPeriod"] = (int?)c.RentPeriod,
            };

            if (c.Negotiable is { } negotiable)
            {
                payload["Negotiable"] = negotiable;
            }

            if (c.AvailableFrom is { } availableFrom)
            {
                payload["AvailableFrom"] = availableFrom.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            }

            return payload;
        }

        var terms = c.RentalTerms!;

        return new { c.Country, c.Region, c.City, c.District, c.Title, c.Description,
            PropertyType = (int)c.PropertyType, Purpose = (int)c.Purpose, c.Amount, c.Currency, RentPeriod = (int?)c.RentPeriod,
            // An option's deposit is written only when stated, so an option without one keeps its old shape.
            PricingOptions = c.PricingOptions.Select(o => o.Deposit is { } deposit
                ? (object)new { Period = (int)o.Period, o.Amount, o.Currency, Deposit = deposit }
                : new { Period = (int)o.Period, o.Amount, o.Currency }).ToArray(),
            RentalTerms = new
            {
                terms.TimeZoneId,
                CheckInTime = terms.CheckInTime.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                CheckOutTime = terms.CheckOutTime.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                terms.MinimumStayDays,
                Windows = terms.Windows.Select(w => new
                {
                    StartDate = w.StartDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    EndDate = w.EndDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                }).ToArray(),
            },
        };
    }

    private static ListingSubmissionContent ReadContent(JsonElement p)
    {
        RentPricingOption[]? options = null;
        RentalTerms? terms = null;

        if (p.TryGetProperty("PricingOptions", out var optionsElement) && optionsElement.ValueKind == JsonValueKind.Array)
        {
            options = optionsElement.EnumerateArray()
                .Select(o => new RentPricingOption((RentPeriod)o.GetProperty("Period").GetInt32(), o.GetProperty("Amount").GetDecimal(), Text(o, "Currency"),
                    o.TryGetProperty("Deposit", out var deposit) && deposit.ValueKind == JsonValueKind.Number ? deposit.GetDecimal() : null))
                .ToArray();
        }

        if (p.TryGetProperty("RentalTerms", out var termsElement) && termsElement.ValueKind == JsonValueKind.Object)
        {
            terms = new RentalTerms(
                Text(termsElement, "TimeZoneId"),
                TimeOnly.ParseExact(Text(termsElement, "CheckInTime"), "HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                TimeOnly.ParseExact(Text(termsElement, "CheckOutTime"), "HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                termsElement.GetProperty("Windows").EnumerateArray().Select(w => new RentalWindow(
                    DateOnly.ParseExact(Text(w, "StartDate"), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    DateOnly.ParseExact(Text(w, "EndDate"), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))),
                termsElement.GetProperty("MinimumStayDays").ValueKind == JsonValueKind.Null
                    ? null
                    : termsElement.GetProperty("MinimumStayDays").GetInt32());
        }

        return new ListingSubmissionContent(Text(p, "Country"), Text(p, "Region"), Text(p, "City"), Text(p, "District"),
            Text(p, "Title"), Text(p, "Description"),
            (PropertyType)p.GetProperty("PropertyType").GetInt32(), (ListingPurpose)p.GetProperty("Purpose").GetInt32(),
            p.GetProperty("Amount").GetDecimal(), Text(p, "Currency"),
            p.GetProperty("RentPeriod").ValueKind == JsonValueKind.Null ? null : (RentPeriod)p.GetProperty("RentPeriod").GetInt32(),
            options, terms,
            p.TryGetProperty("Negotiable", out var negotiable) && negotiable.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? negotiable.GetBoolean()
                : null,
            p.TryGetProperty("AvailableFrom", out var availableFrom) && availableFrom.ValueKind == JsonValueKind.String
                ? DateOnly.ParseExact(availableFrom.GetString()!, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
                : null);
    }
    private static object OwnerData(ListingOwnerIdentity o) => new { Kind = (int)o.Kind, o.LegalPartyReferenceId, o.LinkedUserId };
    private static object EvidenceData(ListingEvidenceReference e) => new { e.EvidenceId, e.VersionId, e.PropertyId, Purpose = (int)e.Purpose, e.Sha256 };
    private static ListingOwnerIdentity Owner(JsonElement p) => new((ListingOwnerKind)p.GetProperty("Kind").GetInt32(),Id(p,"LegalPartyReferenceId"),OptionalId(p,"LinkedUserId"));
    private static ListingEvidenceReference Evidence(JsonElement p) => new(Id(p,"EvidenceId"),Id(p,"VersionId"),Id(p,"PropertyId"),(ListingPurpose)p.GetProperty("Purpose").GetInt32(),Text(p,"Sha256"));
    private static Guid Id(JsonElement p,string key) => p.GetProperty(key).GetGuid();
    private static Guid? OptionalId(JsonElement p,string key) => p.GetProperty(key).ValueKind == JsonValueKind.Null ? null : Id(p,key);
    private static DateTimeOffset? OptionalTime(JsonElement p,string key) => p.GetProperty(key).ValueKind == JsonValueKind.Null ? null : p.GetProperty(key).GetDateTimeOffset();
    private static bool? Flag(JsonElement p,string key) => p.GetProperty(key).ValueKind == JsonValueKind.Null ? null : p.GetProperty(key).GetBoolean();
    private static string Text(JsonElement p,string key) => p.GetProperty(key).GetString() ?? throw new InvalidOperationException("Missing snapshot text.");
    private static void ValidateUniqueMembers(JsonElement p)
    {
        if (p.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in p.EnumerateObject())
            {
                if (!names.Add(member.Name)) throw new InvalidOperationException("Duplicate snapshot member.");
                ValidateUniqueMembers(member.Value);
            }
        }
        else if (p.ValueKind == JsonValueKind.Array)
            foreach (var child in p.EnumerateArray()) ValidateUniqueMembers(child);
    }
}
