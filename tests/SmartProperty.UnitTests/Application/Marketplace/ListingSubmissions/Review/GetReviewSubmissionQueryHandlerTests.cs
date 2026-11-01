using System.Reflection;
using SmartProperty.Application.Marketplace.ListingSubmissions;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review.Detail;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Marketplace.ListingSubmissions.Review;

public sealed class GetReviewSubmissionQueryHandlerTests
{
    private static readonly Guid Reviewer = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly DateTimeOffset Submitted = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    private readonly FakeReviewerListingSubmissionReadRepository _submissions = new();

    [Fact]
    public async Task AnUnauthenticatedRequestIsRejectedBeforeAnyOtherCheck()
    {
        var authorizer = new FakeReviewReadAuthorizer(true);

        var result = await CreateHandler(userId: null, authorizer).Handle(new GetReviewSubmissionQuery(Guid.NewGuid()));

        AssertError(result, "authentication.unauthorized", ErrorType.Unauthorized);
        Assert.Equal(0, authorizer.CallCount);
        Assert.Equal(0, _submissions.GetCallCount);
    }

    [Fact]
    public async Task AnEmptyUserIdIsRejectedBeforeAnyOtherCheck()
    {
        var authorizer = new FakeReviewReadAuthorizer(true);

        var result = await CreateHandler(Guid.Empty, authorizer).Handle(new GetReviewSubmissionQuery(Guid.NewGuid()));

        AssertError(result, "authentication.unauthorized", ErrorType.Unauthorized);
        Assert.Equal(0, authorizer.CallCount);
        Assert.Equal(0, _submissions.GetCallCount);
    }

    [Fact]
    public async Task AnAuthenticatedUserWhoIsNotAuthorizedIsForbiddenAndTheRepositoryIsNeverCalled()
    {
        var detail = Detail(decision: null);
        _submissions.SetLookup(detail.SubmissionId, ReviewDetailLookup.Found(detail));

        var result = await CreateHandler(Reviewer, new FakeReviewReadAuthorizer(false)).Handle(new GetReviewSubmissionQuery(detail.SubmissionId));

        AssertError(result, "authorization.forbidden", ErrorType.Forbidden);
        Assert.Equal(0, _submissions.GetCallCount);
    }

    [Fact]
    public async Task AFailedAuthorizationLookupIsNeverTurnedIntoAccess()
    {
        var authorizer = new FakeReviewReadAuthorizer(true) { ExceptionToThrow = new InvalidOperationException("lookup failed") };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateHandler(Reviewer, authorizer).Handle(new GetReviewSubmissionQuery(Guid.NewGuid())));

        Assert.Equal(0, _submissions.GetCallCount);
    }

    [Fact]
    public async Task AFoundSubmissionIsReturnedAndTheRepositoryIsAskedForTheAuthenticatedReviewer()
    {
        var detail = Detail(decision: null);
        _submissions.SetLookup(detail.SubmissionId, ReviewDetailLookup.Found(detail));
        var authorizer = new FakeReviewReadAuthorizer(true);

        var result = await CreateHandler(Reviewer, authorizer).Handle(new GetReviewSubmissionQuery(detail.SubmissionId));

        Assert.True(result.IsSuccess);
        Assert.Equal(detail, result.Value);
        Assert.Equal(Reviewer, authorizer.LastUserId);
        Assert.Equal(Reviewer, _submissions.LastGetCall!.ReviewerUserId);
        Assert.Equal(detail.SubmissionId, _submissions.LastGetCall.SubmissionId);
    }

    [Fact]
    public async Task AnUnknownSubmissionIsTheStableNotFound()
    {
        var result = await CreateHandler(Reviewer).Handle(new GetReviewSubmissionQuery(Guid.NewGuid()));

        Assert.True(result.IsFailure);
        Assert.Same(ListingSubmissionErrors.NotFound, result.Error);
        Assert.Equal("listing_submissions.not_found", result.Error!.Code);
        Assert.Equal(ErrorType.NotFound, result.Error.Type);
    }

    [Fact]
    public async Task TheEmptyIdIsNotFoundWithoutAskingTheRepositoryButOnlyAfterAuthorization()
    {
        var authorizer = new FakeReviewReadAuthorizer(true);

        var result = await CreateHandler(Reviewer, authorizer).Handle(new GetReviewSubmissionQuery(Guid.Empty));

        Assert.Same(ListingSubmissionErrors.NotFound, result.Error);
        Assert.Equal(1, authorizer.CallCount);
        Assert.Equal(0, _submissions.GetCallCount);

        var forbidden = await CreateHandler(Reviewer, new FakeReviewReadAuthorizer(false)).Handle(new GetReviewSubmissionQuery(Guid.Empty));

        AssertError(forbidden, "authorization.forbidden", ErrorType.Forbidden);
    }

    [Fact]
    public async Task AReviewerWhoIsNotIndependentIsRefusedWithItsOwnCode()
    {
        var id = Guid.NewGuid();
        _submissions.SetLookup(id, ReviewDetailLookup.NotIndependent());

        var result = await CreateHandler(Reviewer).Handle(new GetReviewSubmissionQuery(id));

        Assert.Same(ReviewErrors.NotIndependent, result.Error);
        AssertError(result, "listing_submissions.reviewer_not_independent", ErrorType.Forbidden);
    }

    [Fact]
    public async Task UnverifiableIndependenceIsRefusedWithItsOwnCode()
    {
        var id = Guid.NewGuid();
        _submissions.SetLookup(id, ReviewDetailLookup.IndependenceUnverifiable());

        var result = await CreateHandler(Reviewer).Handle(new GetReviewSubmissionQuery(id));

        Assert.Same(ReviewErrors.IndependenceUnverifiable, result.Error);
        AssertError(result, "listing_submissions.independence_unverifiable", ErrorType.Forbidden);
    }

    [Fact]
    public void ThreeRefusalOutcomesAreThreeDistinctErrors()
    {
        var notFound = ListingSubmissionErrors.NotFound;
        var notIndependent = ReviewErrors.NotIndependent;
        var unverifiable = ReviewErrors.IndependenceUnverifiable;

        Assert.Equal(3, new[] { notFound.Code, notIndependent.Code, unverifiable.Code }.Distinct().Count());
        Assert.NotEqual(ErrorType.Forbidden, notFound.Type);
        Assert.Equal(ErrorType.Forbidden, notIndependent.Type);
        Assert.Equal(ErrorType.Forbidden, unverifiable.Type);
    }

    [Fact]
    public async Task ARejectionKeepsItsReason()
    {
        var detail = Detail(new ReviewDecisionSummary(Approved: false, "The deed does not cover this parcel.", Submitted.AddDays(1)));
        _submissions.SetLookup(detail.SubmissionId, ReviewDetailLookup.Found(detail));

        var result = await CreateHandler(Reviewer).Handle(new GetReviewSubmissionQuery(detail.SubmissionId));

        Assert.False(result.Value.Decision!.Approved);
        Assert.Equal("The deed does not cover this parcel.", result.Value.Decision.Reason);
    }

    [Fact]
    public async Task AnApprovalNeverCarriesAReasonWhateverIsStored()
    {
        var detail = Detail(new ReviewDecisionSummary(Approved: true, "Internal approval note.", Submitted.AddDays(1)));
        _submissions.SetLookup(detail.SubmissionId, ReviewDetailLookup.Found(detail));

        var result = await CreateHandler(Reviewer).Handle(new GetReviewSubmissionQuery(detail.SubmissionId));

        Assert.True(result.Value.Decision!.Approved);
        Assert.Null(result.Value.Decision.Reason);
    }

    [Fact]
    public async Task AFoundLookupForADifferentSubmissionIsAFaultNotAnAnswer()
    {
        var detail = Detail(decision: null);
        var asked = Guid.NewGuid();
        _submissions.SetLookup(asked, ReviewDetailLookup.Found(detail));

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateHandler(Reviewer).Handle(new GetReviewSubmissionQuery(asked)));
    }

    [Fact]
    public async Task AnUndefinedOutcomeIsNeverAccess()
    {
        var id = Guid.NewGuid();
        _submissions.SetLookup(id, UndefinedLookup());

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateHandler(Reviewer).Handle(new GetReviewSubmissionQuery(id)));
    }

    [Fact]
    public void ALookupCanOnlyBeBuiltThroughItsFactories()
    {
        Assert.Empty(typeof(ReviewDetailLookup).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Equal(ReviewDetailOutcome.NotFound, ReviewDetailLookup.NotFound().Outcome);
        Assert.Null(ReviewDetailLookup.NotFound().Detail);
        Assert.Null(ReviewDetailLookup.NotIndependent().Detail);
        Assert.Null(ReviewDetailLookup.IndependenceUnverifiable().Detail);
        Assert.NotNull(ReviewDetailLookup.Found(Detail(decision: null)).Detail);
        Assert.Throws<ArgumentNullException>(() => ReviewDetailLookup.Found(null!));
    }

    [Fact]
    public async Task TheCancellationTokenReachesTheAuthorizerAndTheRepository()
    {
        using var source = new CancellationTokenSource();
        var authorizer = new FakeReviewReadAuthorizer(true);

        await CreateHandler(Reviewer, authorizer).Handle(new GetReviewSubmissionQuery(Guid.NewGuid()), source.Token);

        Assert.Equal(source.Token, authorizer.CancellationToken);
        Assert.Equal(source.Token, _submissions.CancellationToken);
    }

    [Fact]
    public async Task ARepositoryFailureIsNotSwallowed()
    {
        _submissions.ExceptionToThrow = new InvalidOperationException("database unavailable");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateHandler(Reviewer).Handle(new GetReviewSubmissionQuery(Guid.NewGuid())));

        Assert.Equal("database unavailable", exception.Message);
    }

    [Fact]
    public async Task ANullQueryIsAProgrammingError()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => CreateHandler(Reviewer).Handle(null!));
    }

    private GetReviewSubmissionQueryHandler CreateHandler(Guid? userId, FakeReviewReadAuthorizer? authorizer = null)
    {
        return new GetReviewSubmissionQueryHandler(new FakeCurrentUser(userId), authorizer ?? new FakeReviewReadAuthorizer(true), _submissions);
    }

    // Builds a lookup whose outcome no factory can produce, to prove an unknown outcome is never treated as access.
    private static ReviewDetailLookup UndefinedLookup()
    {
        var constructor = typeof(ReviewDetailLookup).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();

        return (ReviewDetailLookup)constructor.Invoke([(ReviewDetailOutcome)99, null]);
    }

    private static ReviewSubmissionDetail Detail(ReviewDecisionSummary? decision)
    {
        return new ReviewSubmissionDetail(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            ListingPurpose.Rent,
            1,
            ListingSubmissionStatus.PendingReview,
            "Bright apartment",
            "Two bedrooms close to the centre.",
            new PublicListingPrice(5200m, "SAR", RentPeriod.Monthly),
            PropertyType.Unit,
            new PublicListingLocation("SA", "Riyadh Province", "Riyadh", "Al Olaya"),
            ListingRepresentationKind.Owner,
            ListingOwnerKind.Individual,
            Submitted.AddDays(-30),
            Submitted.AddDays(335),
            Submitted,
            Submitted,
            Submitted,
            2,
            true,
            false,
            decision);
    }

    private static void AssertError(Result<ReviewSubmissionDetail> result, string code, ErrorType type)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(code, result.Error!.Code);
        Assert.Equal(type, result.Error.Type);
    }
}
