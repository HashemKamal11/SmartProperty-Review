using SmartProperty.Application.Marketplace.ListingSubmissions.Review;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review.Queue;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Common.Pagination;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Marketplace.ListingSubmissions.Review;

public sealed class GetReviewQueueQueryHandlerTests
{
    private static readonly Guid Reviewer = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Submitter = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Submitted = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    private readonly FakeReviewerListingSubmissionReadRepository _submissions = new();

    [Fact]
    public async Task AnUnauthenticatedRequestIsRejectedBeforeAnyOtherCheck()
    {
        var authorizer = new FakeReviewReadAuthorizer(true);

        var result = await CreateHandler(userId: null, authorizer).Handle(Query());

        AssertUnauthorized(result);
        Assert.Equal(0, authorizer.CallCount);
        Assert.Equal(0, _submissions.ListCallCount);
    }

    [Fact]
    public async Task AnEmptyUserIdIsRejectedBeforeAnyOtherCheck()
    {
        var authorizer = new FakeReviewReadAuthorizer(true);

        var result = await CreateHandler(Guid.Empty, authorizer).Handle(Query());

        AssertUnauthorized(result);
        Assert.Equal(0, authorizer.CallCount);
        Assert.Equal(0, _submissions.ListCallCount);
    }

    [Fact]
    public async Task AnAuthenticatedUserWhoIsNotAuthorizedIsForbiddenAndTheRepositoryIsNeverCalled()
    {
        var authorizer = new FakeReviewReadAuthorizer(false);

        var result = await CreateHandler(Reviewer, authorizer).Handle(Query());

        AssertForbidden(result);
        Assert.Equal(1, authorizer.CallCount);
        Assert.Equal(0, _submissions.ListCallCount);
    }

    [Fact]
    public async Task AuthorizationIsCheckedBeforeValidationSoAForbiddenCallerLearnsNothingAboutTheRequest()
    {
        var authorizer = new FakeReviewReadAuthorizer(false);

        var result = await CreateHandler(Reviewer, authorizer).Handle(Query(status: ListingSubmissionStatus.Draft));

        AssertForbidden(result);
        Assert.Equal(0, _submissions.ListCallCount);
    }

    [Fact]
    public async Task AFailedAuthorizationLookupIsNeverTurnedIntoAccess()
    {
        var authorizer = new FakeReviewReadAuthorizer(true) { ExceptionToThrow = new InvalidOperationException("lookup failed") };

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateHandler(Reviewer, authorizer).Handle(Query()));

        Assert.Equal(0, _submissions.ListCallCount);
    }

    [Fact]
    public async Task TheAuthorizerAndTheRepositoryAreBothAskedAboutTheAuthenticatedReviewerOnly()
    {
        var authorizer = new FakeReviewReadAuthorizer(true);

        await CreateHandler(Reviewer, authorizer).Handle(Query());

        Assert.Equal(Reviewer, authorizer.LastUserId);
        Assert.Equal(Reviewer, _submissions.LastListCall!.ReviewerUserId);
    }

    [Fact]
    public async Task AbsentStatusMeansPendingReview()
    {
        var result = await CreateHandler(Reviewer).Handle(Query());

        Assert.True(result.IsSuccess);
        Assert.Equal(ListingSubmissionStatus.PendingReview, _submissions.LastListCall!.Status);
    }

    [Theory]
    [InlineData(ListingSubmissionStatus.PendingReview)]
    [InlineData(ListingSubmissionStatus.Approved)]
    [InlineData(ListingSubmissionStatus.Rejected)]
    [InlineData(ListingSubmissionStatus.Superseded)]
    public async Task EveryReviewableStatusReachesTheRepository(ListingSubmissionStatus status)
    {
        var result = await CreateHandler(Reviewer).Handle(Query(status: status));

        Assert.True(result.IsSuccess);
        Assert.Equal(status, _submissions.LastListCall!.Status);
    }

    [Fact]
    public async Task ADraftCanNeverEnterTheQueue()
    {
        var result = await CreateHandler(Reviewer).Handle(Query(status: ListingSubmissionStatus.Draft));

        AssertValidationFailure(result);
        Assert.Equal(0, _submissions.ListCallCount);
    }

    [Fact]
    public async Task AnUndefinedStatusIsAValidationFailureWithoutCallingTheRepository()
    {
        var result = await CreateHandler(Reviewer).Handle(Query(status: (ListingSubmissionStatus)99));

        AssertValidationFailure(result);
        Assert.Equal(0, _submissions.ListCallCount);
    }

    [Fact]
    public async Task AnUndefinedPurposeIsAValidationFailureWithoutCallingTheRepository()
    {
        var result = await CreateHandler(Reviewer).Handle(Query(purpose: (ListingPurpose)99));

        AssertValidationFailure(result);
        Assert.Equal(0, _submissions.ListCallCount);
    }

    [Fact]
    public async Task ThePurposeFilterReachesTheRepositoryAndNarrowsTheQueue()
    {
        _submissions.Add(Item("Sale", ListingPurpose.Sale, Submitted), Submitter);
        _submissions.Add(Item("Rent", ListingPurpose.Rent, Submitted.AddMinutes(1)), Submitter);

        var result = await CreateHandler(Reviewer).Handle(Query(purpose: ListingPurpose.Rent));

        var item = Assert.Single(result.Value.Items);
        Assert.Equal("Rent", item.Title);
        Assert.Equal(ListingPurpose.Rent, _submissions.LastListCall!.Purpose);
    }

    [Fact]
    public async Task SubmissionsTheReviewerActedOnOrOwnsAreNeverListed()
    {
        _submissions.Add(Item("Mine", ListingPurpose.Sale, Submitted), Reviewer);
        _submissions.Add(Item("Mine as owner", ListingPurpose.Sale, Submitted.AddMinutes(1)), Submitter, linkedOwner: Reviewer);
        _submissions.Add(Item("Someone else's", ListingPurpose.Sale, Submitted.AddMinutes(2)), Submitter);

        var result = await CreateHandler(Reviewer).Handle(Query());

        var item = Assert.Single(result.Value.Items);
        Assert.Equal("Someone else's", item.Title);
        Assert.Equal(1, result.Value.TotalCount);
    }

    [Fact]
    public async Task ThePendingQueueIsOldestFirstAndItsPagesPartitionWithoutRepeatsOrGaps()
    {
        for (var i = 0; i < 5; i++)
        {
            _submissions.Add(Item($"Pending {i}", ListingPurpose.Sale, Submitted.AddMinutes(i)), Submitter);
        }

        var first = await CreateHandler(Reviewer).Handle(Query(page: 1, pageSize: 2));
        var second = await CreateHandler(Reviewer).Handle(Query(page: 2, pageSize: 2));
        var third = await CreateHandler(Reviewer).Handle(Query(page: 3, pageSize: 2));

        var ids = first.Value.Items.Concat(second.Value.Items).Concat(third.Value.Items).Select(item => item.SubmissionId).ToArray();
        Assert.Equal(5, ids.Length);
        Assert.Equal(5, ids.Distinct().Count());
        Assert.Equal(new[] { "Pending 0", "Pending 1" }, first.Value.Items.Select(item => item.Title).ToArray());
        Assert.Equal(3, first.Value.TotalPages);
        Assert.True(first.Value.HasNextPage);
        Assert.False(third.Value.HasNextPage);
    }

    [Fact]
    public async Task AbsentPageValuesDefaultToTheFirstPageOfTen()
    {
        var result = await CreateHandler(Reviewer).Handle(Query());

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.PageNumber);
        Assert.Equal(10, result.Value.PageSize);
        Assert.Equal(0, _submissions.LastListCall!.Skip);
        Assert.Equal(10, _submissions.LastListCall.Take);
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(-1, 1, 0)]
    [InlineData(int.MinValue, 1, 0)]
    [InlineData(3, 3, 20)]
    [InlineData(PageParameters.MaxPageNumber, PageParameters.MaxPageNumber, (PageParameters.MaxPageNumber - 1) * 10)]
    [InlineData(PageParameters.MaxPageNumber + 1, PageParameters.MaxPageNumber, (PageParameters.MaxPageNumber - 1) * 10)]
    [InlineData(int.MaxValue, PageParameters.MaxPageNumber, (PageParameters.MaxPageNumber - 1) * 10)]
    public async Task ThePageIsClamped(int page, int expectedPage, int expectedSkip)
    {
        var result = await CreateHandler(Reviewer).Handle(Query(page: page));

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedPage, result.Value.PageNumber);
        Assert.Equal(expectedSkip, _submissions.LastListCall!.Skip);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(25, 25)]
    [InlineData(PageParameters.MaxPageSize, PageParameters.MaxPageSize)]
    [InlineData(PageParameters.MaxPageSize + 1, PageParameters.MaxPageSize)]
    [InlineData(int.MaxValue, PageParameters.MaxPageSize)]
    public async Task ThePageSizeIsClamped(int pageSize, int expectedPageSize)
    {
        var result = await CreateHandler(Reviewer).Handle(Query(pageSize: pageSize));

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedPageSize, result.Value.PageSize);
        Assert.Equal(expectedPageSize, _submissions.LastListCall!.Take);
    }

    [Fact]
    public async Task AnEmptyQueueIsASuccessfulEmptyPage()
    {
        var result = await CreateHandler(Reviewer).Handle(Query());

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Items);
        Assert.Equal(0, result.Value.TotalCount);
        Assert.Equal(0, result.Value.TotalPages);
    }

    [Fact]
    public async Task TheCancellationTokenReachesTheAuthorizerAndTheRepository()
    {
        using var source = new CancellationTokenSource();
        var authorizer = new FakeReviewReadAuthorizer(true);

        await CreateHandler(Reviewer, authorizer).Handle(Query(), source.Token);

        Assert.Equal(source.Token, authorizer.CancellationToken);
        Assert.Equal(source.Token, _submissions.CancellationToken);
    }

    [Fact]
    public async Task ARepositoryFailureIsNotSwallowed()
    {
        _submissions.ExceptionToThrow = new InvalidOperationException("database unavailable");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateHandler(Reviewer).Handle(Query()));

        Assert.Equal("database unavailable", exception.Message);
    }

    [Fact]
    public async Task ANullQueryIsAProgrammingError()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => CreateHandler(Reviewer).Handle(null!));
    }

    private GetReviewQueueQueryHandler CreateHandler(Guid? userId, FakeReviewReadAuthorizer? authorizer = null)
    {
        return new GetReviewQueueQueryHandler(new FakeCurrentUser(userId), authorizer ?? new FakeReviewReadAuthorizer(true), _submissions);
    }

    private static GetReviewQueueQuery Query(
        ListingSubmissionStatus? status = null,
        ListingPurpose? purpose = null,
        int? page = null,
        int? pageSize = null)
    {
        return new GetReviewQueueQuery(status, purpose, page, pageSize);
    }

    private static ReviewQueueItem Item(
        string title,
        ListingPurpose purpose,
        DateTimeOffset submittedAt,
        ListingSubmissionStatus status = ListingSubmissionStatus.PendingReview)
    {
        return new ReviewQueueItem(
            Guid.NewGuid(),
            Guid.NewGuid(),
            purpose,
            1,
            status,
            title,
            purpose == ListingPurpose.Rent
                ? new PublicListingPrice(5200m, "SAR", RentPeriod.Monthly)
                : new PublicListingPrice(1250000m, "SAR", null),
            PropertyType.Unit,
            new PublicListingLocation("SA", "Riyadh Province", "Riyadh", "Al Olaya"),
            ListingRepresentationKind.Owner,
            ListingOwnerKind.Individual,
            submittedAt,
            2,
            true);
    }

    private static void AssertUnauthorized(Result<PagedList<ReviewQueueItem>> result)
    {
        Assert.True(result.IsFailure);
        Assert.Equal("authentication.unauthorized", result.Error!.Code);
        Assert.Equal(ErrorType.Unauthorized, result.Error.Type);
    }

    private static void AssertForbidden(Result<PagedList<ReviewQueueItem>> result)
    {
        Assert.True(result.IsFailure);
        Assert.Equal("authorization.forbidden", result.Error!.Code);
        Assert.Equal(ErrorType.Forbidden, result.Error.Type);
    }

    private static void AssertValidationFailure(Result<PagedList<ReviewQueueItem>> result)
    {
        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }
}
