using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.Authority;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.UnitTests.Application.Marketplace.ListingSubmissions.Write;

/// <summary>
/// Where every listing write starts: the property lock, then the grant under it, then the property itself.
/// </summary>
public sealed class ListingWriteGateTests
{
    private static readonly Guid Grantee = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PropertyId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Issuer = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Reviewer = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeListingSubmissionWriteRepository _repository = new();

    [Fact]
    public async Task AnEligibleUserOnAnActivePropertyOpensTheGateWithTheGrantDerivedFacts()
    {
        Arrange(Property(), OwnerGrant());

        var result = await Evaluate();

        Assert.True(result.IsOpen);
        Assert.Null(result.Error);
        Assert.Equal(PropertyId, result.Property!.PropertyId);
        Assert.Equal(Grantee, result.Authority!.ActingUserId);
        Assert.Equal(ListingPurpose.Sale, result.Authority.Purpose);
        Assert.True(result.SubmissionFacts!.LegalAuthorityVerified);
    }

    [Fact]
    public async Task TheLocksAreTakenInTheDocumentedOrderAndForTheRightArguments()
    {
        Arrange(Property(), OwnerGrant());

        await Evaluate();

        // Property row first, grant row second. Nothing is read before the property lock.
        Assert.Equal(new[] { "LockProperty", "GetGrant" }, _repository.Calls);
        Assert.Equal(Grantee, _repository.LastGrantee);
        Assert.Equal(PropertyId, _repository.LastGrantPropertyId);
        Assert.Equal(ListingPurpose.Sale, _repository.LastGrantPurpose);
    }

    [Fact]
    public async Task AnUnknownPropertyIsTheSameNotEligibleAsNoGrantAndNoGrantIsNeverRead()
    {
        Arrange(property: null, grant: null);

        var result = await Evaluate();

        Assert.False(result.IsOpen);
        Assert.Same(WriteErrors.NotEligible, result.Error);
        Assert.Null(result.Property);
        Assert.Equal(new[] { "LockProperty" }, _repository.Calls);
    }

    [Fact]
    public async Task ARepositoryThatAnswersForAnotherPropertyIsTreatedAsNoProperty()
    {
        Arrange(Property() with { PropertyId = Guid.NewGuid() }, OwnerGrant());

        var result = await Evaluate();

        Assert.Same(WriteErrors.NotEligible, result.Error);
        Assert.DoesNotContain("GetGrant", _repository.Calls);
    }

    [Fact]
    public async Task NoGrantMeansNotEligible()
    {
        Arrange(Property(), grant: null);

        var result = await Evaluate();

        Assert.Same(WriteErrors.NotEligible, result.Error);
        Assert.Null(result.Authority);
        Assert.Null(result.SubmissionFacts);
    }

    [Fact]
    public async Task EveryWayAGrantCanBeUnsuitableIsTheSameNotEligible()
    {
        var unsuitable = new[]
        {
            OwnerGrant() with { State = AuthorityGrantState.Revoked, RevokedAt = Now.AddDays(-1) },
            OwnerGrant() with { GranteeUserId = Guid.NewGuid() },
            OwnerGrant() with { PropertyId = Guid.NewGuid() },
            OwnerGrant() with { AllowsSale = false, AllowsRent = true },
            OwnerGrant() with { ValidUntil = Now },
            OwnerGrant() with { ValidFrom = Now.AddDays(1) },
            OwnerGrant() with { IssuedByUserId = Grantee },
            OwnerGrant() with { LegalReviewedByUserId = Issuer },
            OwnerGrant() with { IssuerWasAuthorized = false },
            OwnerGrant() with { Representation = ListingRepresentationKind.WorkspaceRepresentative },
        };

        foreach (var grant in unsuitable)
        {
            Arrange(Property(), grant);

            var result = await Evaluate();

            Assert.Same(WriteErrors.NotEligible, result.Error);
        }
    }

    [Fact]
    public async Task AnIneligibleCallerNeverLearnsAnythingAboutTheProperty()
    {
        var archivedAndReserved = Property() with { IsActive = false, HasMarketplaceReservation = true };
        Arrange(archivedAndReserved, grant: null);

        var result = await Evaluate();

        // Eligibility is decided first, so the answer is the generic one and not "unavailable".
        Assert.Same(WriteErrors.NotEligible, result.Error);
        Assert.NotSame(WriteErrors.PropertyUnavailable, result.Error);
    }

    [Fact]
    public async Task AnEligibleCallerIsToldOnlyThatTheArchivedPropertyCannotBeListed()
    {
        Arrange(Property() with { IsActive = false }, OwnerGrant());

        var result = await Evaluate();

        Assert.Same(WriteErrors.PropertyUnavailable, result.Error);
        Assert.Equal("listing_submissions.property_unavailable", result.Error!.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        Assert.Null(result.Property);
    }

    [Fact]
    public async Task AnEligibleCallerIsToldOnlyThatTheReservedPropertyCannotBeListed()
    {
        Arrange(Property() with { HasMarketplaceReservation = true }, OwnerGrant());

        var result = await Evaluate();

        Assert.Same(WriteErrors.PropertyUnavailable, result.Error);
    }

    [Fact]
    public async Task ARepositoryFailurePropagatesAndIsNeverAnAnswer()
    {
        _repository.ExceptionToThrow = new InvalidOperationException("database unavailable");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Evaluate());

        Assert.Equal("database unavailable", exception.Message);
    }

    [Fact]
    public async Task TheCallersTokenReachesTheLock()
    {
        using var source = new CancellationTokenSource();
        Arrange(Property(), OwnerGrant());

        await ListingWriteGate.EvaluateAsync(_repository, Grantee, PropertyId, ListingPurpose.Sale, Now, source.Token);

        Assert.Equal(source.Token, _repository.CancellationToken);
    }

    [Fact]
    public async Task TheGateCannotBeOpenedByAnythingTheCallerSupplies()
    {
        // The only caller inputs are the authenticated user, the property, the purpose and the time. A grant for
        // someone else never opens the gate for this user, however valid it looks.
        Arrange(Property(), OwnerGrant() with { GranteeUserId = Guid.NewGuid() });

        var result = await Evaluate();

        Assert.False(result.IsOpen);
    }

    [Fact]
    public async Task ARepositoryIsRequired()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ListingWriteGate.EvaluateAsync(null!, Grantee, PropertyId, ListingPurpose.Sale, Now));
    }

    private void Arrange(LockedPropertyView? property, AuthorityGrantView? grant)
    {
        _repository.Property = property;
        _repository.Grant = grant;
        _repository.Calls.Clear();
    }

    private Task<ListingWriteGateResult> Evaluate()
    {
        return ListingWriteGate.EvaluateAsync(_repository, Grantee, PropertyId, ListingPurpose.Sale, Now);
    }

    private static LockedPropertyView Property()
    {
        return new LockedPropertyView(PropertyId, true, false, PropertyType.Unit, "SA", "Riyadh Province", "Riyadh", "Al Olaya");
    }

    private static AuthorityGrantView OwnerGrant()
    {
        return new AuthorityGrantView(
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            7,
            Grantee,
            PropertyId,
            AllowsSale: true,
            AllowsRent: false,
            ListingOwnerKind.Individual,
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            LinkedUserId: Grantee,
            ListingRepresentationKind.Owner,
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 9, 1, 0, 0, 0, TimeSpan.Zero),
            AuthorityGrantState.Active,
            RevokedAt: null,
            Issuer,
            new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero),
            IssuerWasAuthorized: true,
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            Reviewer,
            new DateTimeOffset(2026, 8, 15, 9, 0, 0, TimeSpan.Zero));
    }
}
