using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Authentication.Me;
using SmartProperty.Application.Marketplace.ListingSubmissions;
using SmartProperty.Application.Marketplace.ListingSubmissions.Mine;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Common.Pagination;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Marketplace.ListingSubmissions;

public sealed class GetMyListingSubmissionsQueryHandlerTests
{
    private static readonly Guid Me = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SomeoneElse = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Created = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    private readonly FakeSubmitterListingSubmissionReadRepository _submissions = new();
    private readonly FakeUserRepository _users = new();

    // ----- the account behind the token

    [Fact]
    public async Task AMissingAccountIsA401AndTheSubmissionsAreNeverRead()
    {
        var result = await CreateHandler(Me, accountStatus: null).Handle(Query());

        AssertUnauthorized(result);
        Assert.Equal(1, _users.GetByIdCallCount);
        Assert.Equal(0, _submissions.ListCallCount);
    }

    [Theory]
    [InlineData(UserStatus.Pending)]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Deactivated)]
    public async Task ANonActiveAccountIsA403AccountUnavailableAndTheSubmissionsAreNeverRead(UserStatus status)
    {
        _submissions.Add(Me, Detail("Mine", Created));

        var result = await CreateHandler(Me, status).Handle(Query());

        Assert.True(result.IsFailure);
        Assert.Same(MeErrors.AccountUnavailable, result.Error);
        Assert.Equal("authentication.account_unavailable", result.Error!.Code);
        Assert.Equal(ErrorType.Forbidden, result.Error.Type);
        Assert.Equal(0, _submissions.ListCallCount);
    }

    [Fact]
    public async Task AnActiveAccountProceedsToTheRepositoryForTheAuthenticatedUser()
    {
        _submissions.Add(Me, Detail("Mine", Created));

        var result = await CreateHandler(Me, UserStatus.Active).Handle(Query());

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Items);
        Assert.Equal(1, _users.GetByIdCallCount);
        Assert.Equal(Me, _submissions.LastListCall!.ActingUserId);
    }

    [Fact]
    public async Task TheAccountIsCheckedBeforeTheQueryIsValidated()
    {
        var invalid = Query(purpose: (ListingPurpose)99);

        var inactive = await CreateHandler(Me, UserStatus.Suspended).Handle(invalid);
        var missing = await CreateHandler(SomeoneElse, accountStatus: null).Handle(invalid);

        Assert.Same(MeErrors.AccountUnavailable, inactive.Error);
        AssertUnauthorized(missing);
        Assert.Equal(0, _submissions.ListCallCount);
    }

    [Fact]
    public async Task NoAccountLookupIsMadeWithoutAUsableSubject()
    {
        await CreateHandler(userId: null).Handle(Query());
        await CreateHandler(userId: Guid.Empty).Handle(Query());

        Assert.Equal(0, _users.GetByIdCallCount);
    }

    [Fact]
    public async Task TheAccountIsLookedUpForTheAuthenticatedUserWithTheCallersToken()
    {
        using var source = new CancellationTokenSource();
        var accounts = new RecordingUserRepository(UserStatus.Active);

        await CreateHandler(Me, users: accounts).Handle(Query(), source.Token);

        Assert.Equal(Me, accounts.LastRequestedId);
        Assert.Equal(source.Token, accounts.CancellationToken);
    }

    [Fact]
    public async Task AFailedAccountLookupPropagatesAndNeverBecomesAccess()
    {
        var accounts = new RecordingUserRepository(UserStatus.Active) { ExceptionToThrow = new InvalidOperationException("identity unavailable") };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateHandler(Me, users: accounts).Handle(Query()));

        Assert.Equal("identity unavailable", exception.Message);
        Assert.Equal(0, _submissions.ListCallCount);
    }

    [Fact]
    public async Task AnUnauthenticatedRequestIsRejectedWithoutCallingTheRepository()
    {
        var result = await CreateHandler(userId: null).Handle(Query());

        AssertUnauthorized(result);
        Assert.Equal(0, _submissions.ListCallCount);
    }

    [Fact]
    public async Task AnEmptyUserIdIsRejectedWithoutCallingTheRepository()
    {
        var result = await CreateHandler(userId: Guid.Empty).Handle(Query());

        AssertUnauthorized(result);
        Assert.Equal(0, _submissions.ListCallCount);
    }

    [Fact]
    public async Task TheRepositoryIsAskedOnlyForTheAuthenticatedUser()
    {
        await CreateHandler(Me).Handle(Query());

        Assert.Equal(Me, _submissions.LastListCall!.ActingUserId);
    }

    [Fact]
    public async Task OnlyTheAuthenticatedUsersOwnSubmissionsAreReturned()
    {
        var mine = Detail("Mine", Created);
        var theirs = Detail("Theirs", Created.AddMinutes(1));
        _submissions.Add(Me, mine);
        _submissions.Add(SomeoneElse, theirs);

        var result = await CreateHandler(Me).Handle(Query());

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value.Items);
        Assert.Equal(mine.SubmissionId, item.SubmissionId);
        Assert.Equal(1, result.Value.TotalCount);
    }

    [Fact]
    public async Task AnotherUserSeesTheirOwnSubmissionsAndNotMine()
    {
        _submissions.Add(Me, Detail("Mine", Created));
        _submissions.Add(SomeoneElse, Detail("Theirs", Created));

        var result = await CreateHandler(SomeoneElse).Handle(Query());

        var item = Assert.Single(result.Value.Items);
        Assert.Equal("Theirs", item.Title);
    }

    [Fact]
    public async Task AUserWithNoSubmissionsGetsAnEmptyPage()
    {
        _submissions.Add(SomeoneElse, Detail("Theirs", Created));

        var result = await CreateHandler(Me).Handle(Query());

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Items);
        Assert.Equal(0, result.Value.TotalCount);
        Assert.Equal(0, result.Value.TotalPages);
    }

    [Fact]
    public async Task AbsentPageValuesDefaultToTheFirstPageOfTen()
    {
        var result = await CreateHandler(Me).Handle(Query());

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
        var result = await CreateHandler(Me).Handle(Query(page: page));

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
        var result = await CreateHandler(Me).Handle(Query(pageSize: pageSize));

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedPageSize, result.Value.PageSize);
        Assert.Equal(expectedPageSize, _submissions.LastListCall!.Take);
    }

    [Fact]
    public async Task ThePagesPartitionTheOwnSubmissionsWithoutRepeatsOrGaps()
    {
        for (var i = 0; i < 5; i++)
        {
            _submissions.Add(Me, Detail($"Mine {i}", Created.AddMinutes(i)));
        }

        var first = await CreateHandler(Me).Handle(Query(page: 1, pageSize: 2));
        var second = await CreateHandler(Me).Handle(Query(page: 2, pageSize: 2));
        var third = await CreateHandler(Me).Handle(Query(page: 3, pageSize: 2));

        var ids = first.Value.Items.Concat(second.Value.Items).Concat(third.Value.Items).Select(item => item.SubmissionId).ToArray();
        Assert.Equal(5, ids.Length);
        Assert.Equal(5, ids.Distinct().Count());
        Assert.Equal(3, first.Value.TotalPages);
        Assert.True(first.Value.HasNextPage);
        Assert.False(third.Value.HasNextPage);
        Assert.Equal(new[] { "Mine 4", "Mine 3" }, first.Value.Items.Select(item => item.Title).ToArray());
    }

    [Fact]
    public async Task FiltersReachTheRepositoryAndNarrowOnlyTheOwnRows()
    {
        _submissions.Add(Me, Detail("Sale approved", Created, ListingPurpose.Sale, ListingSubmissionStatus.Approved));
        _submissions.Add(Me, Detail("Rent pending", Created.AddMinutes(1), ListingPurpose.Rent, ListingSubmissionStatus.PendingReview));
        _submissions.Add(SomeoneElse, Detail("Other rent pending", Created, ListingPurpose.Rent, ListingSubmissionStatus.PendingReview));

        var result = await CreateHandler(Me).Handle(Query(purpose: ListingPurpose.Rent, status: ListingSubmissionStatus.PendingReview));

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value.Items);
        Assert.Equal("Rent pending", item.Title);
        Assert.Equal(ListingPurpose.Rent, _submissions.LastListCall!.Purpose);
        Assert.Equal(ListingSubmissionStatus.PendingReview, _submissions.LastListCall.Status);
    }

    [Fact]
    public async Task AnUndefinedPurposeIsAValidationFailureWithoutCallingTheRepository()
    {
        var result = await CreateHandler(Me).Handle(Query(purpose: (ListingPurpose)99));

        AssertValidationFailure(result);
        Assert.Equal(0, _submissions.ListCallCount);
    }

    [Fact]
    public async Task AnUndefinedStatusIsAValidationFailureWithoutCallingTheRepository()
    {
        var result = await CreateHandler(Me).Handle(Query(status: (ListingSubmissionStatus)99));

        AssertValidationFailure(result);
        Assert.Equal(0, _submissions.ListCallCount);
    }

    [Fact]
    public async Task TheRepositoryItemsPassThroughUntouched()
    {
        var detail = Detail("Mine", Created);
        _submissions.Add(Me, detail);

        var result = await CreateHandler(Me).Handle(Query());

        var item = Assert.Single(result.Value.Items);
        Assert.Equal(detail.Title, item.Title);
        Assert.Equal(detail.Price, item.Price);
        Assert.Equal(detail.Location, item.Location);
        Assert.Equal(detail.Status, item.Status);
        Assert.Equal(detail.IsCurrentlyPublished, item.IsCurrentlyPublished);
    }

    [Fact]
    public async Task TheCancellationTokenReachesTheRepository()
    {
        using var source = new CancellationTokenSource();

        await CreateHandler(Me).Handle(Query(), source.Token);

        Assert.Equal(source.Token, _submissions.CancellationToken);
    }

    [Fact]
    public async Task ARepositoryFailureIsNotSwallowed()
    {
        _submissions.ExceptionToThrow = new InvalidOperationException("database unavailable");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateHandler(Me).Handle(Query()));

        Assert.Equal("database unavailable", exception.Message);
    }

    [Fact]
    public async Task ANullQueryIsAProgrammingError()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => CreateHandler(Me).Handle(null!));
    }

    // By default the caller has an Active account, so the tests about submissions need not repeat it. Pass a status
    // to test another account state, null for no account at all, or a user repository of your own.
    private GetMyListingSubmissionsQueryHandler CreateHandler(
        Guid? userId,
        UserStatus? accountStatus = UserStatus.Active,
        IUserRepository? users = null)
    {
        if (users is null && userId is { } id && id != Guid.Empty && accountStatus is { } status)
        {
            SeedAccount(id, status);
        }

        return new GetMyListingSubmissionsQueryHandler(new FakeCurrentUser(userId), users ?? _users, _submissions);
    }

    private void SeedAccount(Guid id, UserStatus status)
    {
        var user = new User(id, $"{id}@example.test", "Test", "User", Created);

        switch (status)
        {
            case UserStatus.Active:
                user.Activate(Created);
                break;
            case UserStatus.Suspended:
                user.Suspend(Created);
                break;
            case UserStatus.Deactivated:
                user.Deactivate(Created);
                break;
            case UserStatus.Pending:
            default:
                break;
        }

        _users.Seed(user);
    }

    private sealed class RecordingUserRepository(UserStatus status) : IUserRepository
    {
        public Exception? ExceptionToThrow { get; set; }

        public Guid? LastRequestedId { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            LastRequestedId = id;
            CancellationToken = cancellationToken;

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            var user = new User(id, $"{id}@example.test", "Test", "User", Created);

            if (status == UserStatus.Active)
            {
                user.Activate(Created);
            }

            return Task.FromResult<User?>(user);
        }

        public Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task AddAsync(User user, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private static GetMyListingSubmissionsQuery Query(
        ListingPurpose? purpose = null,
        ListingSubmissionStatus? status = null,
        int? page = null,
        int? pageSize = null)
    {
        return new GetMyListingSubmissionsQuery(purpose, status, page, pageSize);
    }

    private static SubmitterSubmissionDetail Detail(
        string title,
        DateTimeOffset createdAt,
        ListingPurpose purpose = ListingPurpose.Sale,
        ListingSubmissionStatus status = ListingSubmissionStatus.PendingReview)
    {
        return new SubmitterSubmissionDetail(
            Guid.NewGuid(),
            Guid.NewGuid(),
            purpose,
            1,
            status,
            title,
            "A description.",
            purpose == ListingPurpose.Rent
                ? new PublicListingPrice(5200m, "SAR", RentPeriod.Monthly)
                : new PublicListingPrice(1250000m, "SAR", null),
            PropertyType.Unit,
            new PublicListingLocation("SA", "Riyadh Province", "Riyadh", "Al Olaya"),
            ListingRepresentationKind.Owner,
            createdAt,
            createdAt,
            createdAt,
            false,
            null);
    }

    private static void AssertUnauthorized(Result<PagedList<SubmitterSubmissionSummary>> result)
    {
        Assert.True(result.IsFailure);
        Assert.Equal("authentication.unauthorized", result.Error!.Code);
        Assert.Equal(ErrorType.Unauthorized, result.Error.Type);
    }

    private static void AssertValidationFailure(Result<PagedList<SubmitterSubmissionSummary>> result)
    {
        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }
}
