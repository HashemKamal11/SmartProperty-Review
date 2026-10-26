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
            ListingSubmissionContent c => new { c.Country, c.Region, c.City, c.District, c.Title, c.Description,
                PropertyType = (int)c.PropertyType, Purpose = (int)c.Purpose, c.Amount, c.Currency, RentPeriod = (int?)c.RentPeriod },
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
            value = new ListingSubmissionContent(Text(p,"Country"),Text(p,"Region"),Text(p,"City"),Text(p,"District"),Text(p,"Title"),Text(p,"Description"),
                (PropertyType)p.GetProperty("PropertyType").GetInt32(),(ListingPurpose)p.GetProperty("Purpose").GetInt32(),p.GetProperty("Amount").GetDecimal(),Text(p,"Currency"),
                p.GetProperty("RentPeriod").ValueKind == JsonValueKind.Null ? null : (RentPeriod)p.GetProperty("RentPeriod").GetInt32());
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
