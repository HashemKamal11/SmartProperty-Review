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

    // Every code that existed before the authority codes, spelled out so a rename cannot hide.
    private static readonly string[] ExistingCodes =
    [
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

    [Fact]
    public void TheCatalogIsExactlyTheTwelveCodesWithTheirDescriptionsAndEachCodeIsUnique()
    {
        var constants = typeof(PermissionCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(field => field.IsLiteral)
            .ToArray();
        var codes = constants.Where(field => !field.Name.EndsWith("Description", StringComparison.Ordinal)).Select(field => (string)field.GetRawConstantValue()!).ToArray();
        var descriptions = constants.Where(field => field.Name.EndsWith("Description", StringComparison.Ordinal)).Select(field => (string)field.GetRawConstantValue()!).ToArray();

        Assert.Equal(12, codes.Length);
        Assert.Equal(12, descriptions.Length);
        Assert.Equal(12, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            ExistingCodes.Concat(AuthorityCodes).Order(StringComparer.Ordinal),
            codes.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ThereIsNoWorkspaceScopedAuthorityCode()
    {
        // Workspace-scoped codes are the ones the approval path attaches; none of these is among them.
        Assert.DoesNotContain(PermissionCodes.WorkspaceRead, AuthorityCodes);
        Assert.All(AuthorityCodes, code => Assert.DoesNotContain("workspace", code, StringComparison.OrdinalIgnoreCase));
    }
}
