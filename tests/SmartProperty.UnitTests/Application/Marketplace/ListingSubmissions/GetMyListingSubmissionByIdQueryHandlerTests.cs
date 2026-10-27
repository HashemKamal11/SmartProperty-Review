using SmartProperty.Application.Marketplace.ListingSubmissions;
using SmartProperty.Application.Marketplace.ListingSubmissions.GetById;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Marketplace.ListingSubmissions;

public sealed class GetMyListingSubmissionByIdQueryHandlerTests
{
    private static readonly Guid Me = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SomeoneElse = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Created = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    private readonly FakeSubmitterListingSubmissionReadRepository _submissions = new();

    [Fact]
    public async Task AnUnauthenticatedRequestIsRejectedWithoutCallingTheRepository()
    {
        var detail = Seed(Me);

        var result = await CreateHandler(userId: null).Handle(new GetMyListingSubmissionByIdQuery(detail.SubmissionId));

        AssertUnauthorized(result);
        Assert.Equal(0, _submissions.GetCallCount);
    }

    [Fact]
    public async Task AnEmptyUserIdIsRejectedWithoutCallingTheRepository()
    {
        var detail = Seed(Me);

        var result = await CreateHandler(userId: Guid.Empty).Handle(new GetMyListingSubmissionByIdQuery(detail.SubmissionId));

        AssertUnauthorized(result);
        Assert.Equal(0, _submissions.GetCallCount);
    }

    [Fact]
    public async Task TheOwnSubmissionIsReturnedAndTheRepositoryIsAskedForTheAuthenticatedUser()
    {
        var detail = Seed(Me);

        var result = await CreateHandler(Me).Handle(new GetMyListingSubmissionByIdQuery(detail.SubmissionId));

        Assert.True(result.IsSuccess);
        Assert.Equal(detail, result.Value);
        Assert.Equal(Me, _submissions.LastGetCall!.ActingUserId);
        Assert.Equal(detail.SubmissionId, _submissions.LastGetCall.SubmissionId);
    }

    [Fact]
    public async Task AnotherUsersSubmissionIsTheSameNotFoundAsAMissingOne()
    {
        var theirs = Seed(SomeoneElse);

        var foreign = await CreateHandler(Me).Handle(new GetMyListingSubmissionByIdQuery(theirs.SubmissionId));
        var missing = await CreateHandler(Me).Handle(new GetMyListingSubmissionByIdQuery(Guid.NewGuid()));

        Assert.True(foreign.IsFailure);
        Assert.True(missing.IsFailure);
        Assert.Same(ListingSubmissionErrors.NotFound, foreign.Error);
        Assert.Same(foreign.Error, missing.Error);
        Assert.Equal("listing_submissions.not_found", foreign.Error!.Code);
        Assert.Equal("The listing submission was not found.", foreign.Error.Description);
        Assert.Equal(ErrorType.NotFound, foreign.Error.Type);
    }

    [Fact]
    public async Task TheOwnerOfAnotherSubmissionStillSeesTheirOwn()
    {
        var theirs = Seed(SomeoneElse);

        var result = await CreateHandler(SomeoneElse).Handle(new GetMyListingSubmissionByIdQuery(theirs.SubmissionId));

        Assert.True(result.IsSuccess);
        Assert.Equal(theirs.SubmissionId, result.Value.SubmissionId);
    }

    [Fact]
    public async Task TheEmptyIdIsNotFoundWithoutAskingTheRepository()
    {
        var result = await CreateHandler(Me).Handle(new GetMyListingSubmissionByIdQuery(Guid.Empty));

        Assert.True(result.IsFailure);
        Assert.Same(ListingSubmissionErrors.NotFound, result.Error);
        Assert.Equal(0, _submissions.GetCallCount);
    }

    [Fact]
    public async Task ARejectionKeepsItsReasonForTheSubmitter()
    {
        var detail = Seed(Me, new SubmitterSubmissionDecision(Approved: false, "The deed does not cover this parcel.", Created.AddDays(1)), ListingSubmissionStatus.Rejected);

        var result = await CreateHandler(Me).Handle(new GetMyListingSubmissionByIdQuery(detail.SubmissionId));

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Decision!.Approved);
        Assert.Equal("The deed does not cover this parcel.", result.Value.Decision.Reason);
        Assert.Equal(Created.AddDays(1), result.Value.Decision.DecidedAt);
    }

    [Fact]
    public async Task AnApprovalNeverExposesAReasonWhateverIsStored()
    {
        var detail = Seed(Me, new SubmitterSubmissionDecision(Approved: true, "Internal approval note.", Created.AddDays(1)), ListingSubmissionStatus.Approved);

        var result = await CreateHandler(Me).Handle(new GetMyListingSubmissionByIdQuery(detail.SubmissionId));

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.Decision!.Approved);
        Assert.Null(result.Value.Decision.Reason);
        Assert.Equal(Created.AddDays(1), result.Value.Decision.DecidedAt);
    }

    [Fact]
    public async Task ASubmissionWithoutADecisionHasNone()
    {
        var detail = Seed(Me);

        var result = await CreateHandler(Me).Handle(new GetMyListingSubmissionByIdQuery(detail.SubmissionId));

        Assert.Null(result.Value.Decision);
    }

    [Fact]
    public async Task ARepositoryAnswerForADifferentSubmissionIsAFaultNotANotFound()
    {
        _submissions.ForcedDetail = Detail(Created, decision: null, ListingSubmissionStatus.PendingReview);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateHandler(Me).Handle(new GetMyListingSubmissionByIdQuery(Guid.NewGuid())));
    }

    [Fact]
    public async Task TheCancellationTokenReachesTheRepository()
    {
        using var source = new CancellationTokenSource();
        var detail = Seed(Me);

        await CreateHandler(Me).Handle(new GetMyListingSubmissionByIdQuery(detail.SubmissionId), source.Token);

        Assert.Equal(source.Token, _submissions.CancellationToken);
    }

    [Fact]
    public async Task ARepositoryFailureIsNotSwallowed()
    {
        _submissions.ExceptionToThrow = new InvalidOperationException("database unavailable");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateHandler(Me).Handle(new GetMyListingSubmissionByIdQuery(Guid.NewGuid())));

        Assert.Equal("database unavailable", exception.Message);
    }

    [Fact]
    public async Task ANullQueryIsAProgrammingError()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => CreateHandler(Me).Handle(null!));
    }

    private GetMyListingSubmissionByIdQueryHandler CreateHandler(Guid? userId)
    {
        return new GetMyListingSubmissionByIdQueryHandler(new FakeCurrentUser(userId), _submissions);
    }

    private SubmitterSubmissionDetail Seed(
        Guid owner,
        SubmitterSubmissionDecision? decision = null,
        ListingSubmissionStatus status = ListingSubmissionStatus.PendingReview)
    {
        var detail = Detail(Created, decision, status);
        _submissions.Add(owner, detail);

        return detail;
    }

    private static SubmitterSubmissionDetail Detail(
        DateTimeOffset createdAt,
        SubmitterSubmissionDecision? decision,
        ListingSubmissionStatus status)
    {
        return new SubmitterSubmissionDetail(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ListingPurpose.Rent,
            2,
            status,
            "Bright apartment",
            "Two bedrooms close to the centre.",
            new PublicListingPrice(5200m, "SAR", RentPeriod.Monthly),
            PropertyType.Unit,
            new PublicListingLocation("SA", "Riyadh Province", "Riyadh", "Al Olaya"),
            ListingRepresentationKind.IndividualRepresentative,
            createdAt,
            createdAt,
            createdAt.AddDays(1),
            false,
            decision);
    }

    private static void AssertUnauthorized(Result<SubmitterSubmissionDetail> result)
    {
        Assert.True(result.IsFailure);
        Assert.Equal("authentication.unauthorized", result.Error!.Code);
        Assert.Equal(ErrorType.Unauthorized, result.Error.Type);
    }
}
