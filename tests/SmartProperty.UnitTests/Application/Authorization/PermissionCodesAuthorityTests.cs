using System.Reflection;
using SmartProperty.Application.Authorization;
using Xunit;

namespace SmartProperty.UnitTests.Application.Authorization;

/// <summary>
/// The four property authority permission definitions: their exact spelling, their independence from one another and
/// from every other permission, and the shape of the catalog around them.
/// </summary>
/// <remarks>
/// These are definitions only. Nothing here, and nothing in the platform administrator bootstrap, creates a row for
/// them or assigns them to anyone.
/// </remarks>
public sealed class PermissionCodesAuthorityTests
{
    private static readonly string[] AuthorityCodes =
    [
        PermissionCodes.MarketplaceAuthorityLegalReviewsRecord,
        PermissionCodes.MarketplaceAuthorityGrantsIssue,
        PermissionCodes.MarketplaceAuthorityGrantsRevoke,
        PermissionCodes.MarketplaceAuthorityGrantsRead,
    ];

    private static readonly string[] AuthorityDescriptions =
    [
        PermissionCodes.MarketplaceAuthorityLegalReviewsRecordDescription,
        PermissionCodes.MarketplaceAuthorityGrantsIssueDescription,
        PermissionCodes.MarketplaceAuthorityGrantsRevokeDescription,
        PermissionCodes.MarketplaceAuthorityGrantsReadDescription,
    ];

    // Every code other than the authority codes, spelled out so a rename cannot hide.
    private static readonly string[] ExistingCodes =
    [
        "workspace.admin.read",
        "workspace.admin.create",
        "property.create",
        "property.read",
        "property.update",
        "property.archive",
        "workspace.access_requests.review",
        "marketplace.submissions.review.read",
        "marketplace.submissions.review.decide",
        "workspace.read",
    ];

    [Fact]
    public void TheFourCodesAreExactlyTheApprovedSpelling()
    {
        Assert.Equal("marketplace.authority.legal_reviews.record", PermissionCodes.MarketplaceAuthorityLegalReviewsRecord);
        Assert.Equal("marketplace.authority.grants.issue", PermissionCodes.MarketplaceAuthorityGrantsIssue);
        Assert.Equal("marketplace.authority.grants.revoke", PermissionCodes.MarketplaceAuthorityGrantsRevoke);
        Assert.Equal("marketplace.authority.grants.read", PermissionCodes.MarketplaceAuthorityGrantsRead);
    }

    [Fact]
    public void TheFourDescriptionsAreExactlyTheApprovedText()
    {
        Assert.Equal("Record legal reviews for property authority grants.", PermissionCodes.MarketplaceAuthorityLegalReviewsRecordDescription);
        Assert.Equal("Issue property authority grants.", PermissionCodes.MarketplaceAuthorityGrantsIssueDescription);
        Assert.Equal("Revoke property authority grants.", PermissionCodes.MarketplaceAuthorityGrantsRevokeDescription);
        Assert.Equal("Read property authority grants and legal reviews.", PermissionCodes.MarketplaceAuthorityGrantsReadDescription);
    }

    [Fact]
    public void EveryCodeAndDescriptionIsNonBlankTrimmedAndDistinct()
    {
        foreach (var value in AuthorityCodes.Concat(AuthorityDescriptions))
        {
            Assert.False(string.IsNullOrWhiteSpace(value));
            Assert.Equal(value.Trim(), value);
        }

        Assert.Equal(4, AuthorityCodes.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(4, AuthorityDescriptions.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryCodeIsLowerCaseAndInTheAuthorityNamespace()
    {
        foreach (var code in AuthorityCodes)
        {
            Assert.StartsWith("marketplace.authority.", code, StringComparison.Ordinal);
            Assert.Equal(code.ToLowerInvariant(), code);
            Assert.False(code.Contains(' ', StringComparison.Ordinal));
        }
    }

    [Fact]
    public void NoAuthorityCodeEqualsContainsOrIsAPrefixOfAnother()
    {
        foreach (var code in AuthorityCodes)
        {
            foreach (var other in AuthorityCodes.Where(candidate => candidate != code))
            {
                Assert.False(code.Contains(other, StringComparison.Ordinal), $"'{code}' contains '{other}'.");
                Assert.False(code.StartsWith(other, StringComparison.Ordinal), $"'{code}' starts with '{other}'.");
            }
        }
    }

    [Fact]
    public void NoAuthorityCodeCollidesWithOrImpliesAnyExistingCode()
    {
        foreach (var code in AuthorityCodes)
        {
            foreach (var existing in ExistingCodes)
            {
                Assert.NotEqual(existing, code);
                Assert.False(code.StartsWith(existing, StringComparison.Ordinal), $"'{code}' starts with '{existing}'.");
                Assert.False(existing.StartsWith(code, StringComparison.Ordinal), $"'{existing}' starts with '{code}'.");
            }
        }
    }

    [Fact]
    public void TheListingReviewPermissionsAreUntouched()
    {
        Assert.Equal("marketplace.submissions.review.read", PermissionCodes.MarketplaceSubmissionsReviewRead);
        Assert.Equal("marketplace.submissions.review.decide", PermissionCodes.MarketplaceSubmissionsReviewDecide);
        Assert.Equal("workspace.read", PermissionCodes.WorkspaceRead);
        Assert.Equal("Read the workspace itself.", PermissionCodes.WorkspaceReadDescription);
    }

    // The complete catalog: every code with its exact stored description, spelled out so an addition, a removal, a
    // rename or a reworded description cannot hide.
    private static readonly (string Code, string Description)[] Catalog =
    [
        ("workspace.admin.read", "Read the administrative workspace directory and details."),
        ("workspace.admin.create", "Create workspaces."),
        ("property.create", "Create canonical properties."),
        ("property.read", "Read canonical properties."),
        ("property.update", "Update canonical properties."),
        ("property.archive", "Archive canonical properties."),
        ("workspace.access_requests.review", "Approve or reject workspace access requests."),
        ("marketplace.submissions.review.read", "Read the marketplace listing submission review queue and details."),
        ("marketplace.submissions.review.decide", "Approve or reject marketplace listing submissions."),
        ("marketplace.authority.legal_reviews.record", "Record legal reviews for property authority grants."),
        ("marketplace.authority.grants.issue", "Issue property authority grants."),
        ("marketplace.authority.grants.revoke", "Revoke property authority grants."),
        ("marketplace.authority.grants.read", "Read property authority grants and legal reviews."),
        ("workspace.read", "Read the workspace itself."),
    ];

    [Fact]
    public void TheCatalogIsExactlyTheFourteenCodesWithTheirDescriptionsAndEachCodeIsUnique()
    {
        var constants = typeof(PermissionCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(field => field.IsLiteral)
            .ToArray();
        var values = constants.ToDictionary(field => field.Name, field => (string)field.GetRawConstantValue()!, StringComparer.Ordinal);
        var codeFields = constants.Where(field => !field.Name.EndsWith("Description", StringComparison.Ordinal)).ToArray();
        var descriptionFields = constants.Where(field => field.Name.EndsWith("Description", StringComparison.Ordinal)).ToArray();

        Assert.Equal(14, codeFields.Length);
        Assert.Equal(14, descriptionFields.Length);

        // Each code has exactly its own description constant, and no description stands alone.
        Assert.Equal(
            codeFields.Select(field => field.Name + "Description").Order(StringComparer.Ordinal),
            descriptionFields.Select(field => field.Name).Order(StringComparer.Ordinal));

        var actual = codeFields
            .Select(field => (Code: values[field.Name], Description: values[field.Name + "Description"]))
            .OrderBy(entry => entry.Code, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(Catalog.OrderBy(entry => entry.Code, StringComparer.Ordinal).ToArray(), actual);
        Assert.Equal(14, actual.Select(entry => entry.Code).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(14, actual.Select(entry => entry.Description).Distinct(StringComparer.Ordinal).Count());
        Assert.All(actual, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Code));
            Assert.False(string.IsNullOrWhiteSpace(entry.Description));
            Assert.Equal(entry.Code.Trim(), entry.Code);
            Assert.Equal(entry.Description.Trim(), entry.Description);
        });

        // The partial lists the other tests rely on cover the whole catalog between them.
        Assert.Equal(
            ExistingCodes.Concat(AuthorityCodes).Order(StringComparer.Ordinal),
            Catalog.Select(entry => entry.Code).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ThereIsNoWorkspaceScopedAuthorityCode()
    {
        // Workspace-scoped codes are the ones the approval path attaches; none of these is among them.
        Assert.DoesNotContain(PermissionCodes.WorkspaceRead, AuthorityCodes);
        Assert.All(AuthorityCodes, code => Assert.DoesNotContain("workspace", code, StringComparison.OrdinalIgnoreCase));
    }
}
