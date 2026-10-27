using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Authentication.Me;
using SmartProperty.Application.Marketplace.ListingSubmissions;
using SmartProperty.Application.Marketplace.ListingSubmissions.GetById;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
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
    private readonly FakeUserRepository _users = new();

    // ----- the account behind the token

    [Fact]
    public async Task AMissingAccountIsA401AndTheSubmissionIsNeverRead()
    {
        var detail = Seed(Me);

        var result = await CreateHandler(Me, accountStatus: null).Handle(new GetMyListingSubmissionByIdQuery(detail.SubmissionId));

        AssertUnauthorized(result);
        Assert.Equal(1, _users.GetByIdCallCount);
        Assert.Equal(0, _submissions.GetCallCount);
    }

    [Theory]
    [InlineData(UserStatus.Pending)]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Deactivated)]
    public async Task ANonActiveAccountIsA403AccountUnavailableAndTheSubmissionIsNeverRead(UserStatus status)
    {
        var detail = Seed(Me);

        var result = await CreateHandler(Me, status).Handle(new GetMyListingSubmissionByIdQuery(detail.SubmissionId));

        Assert.True(result.IsFailure);
        Assert.Same(MeErrors.AccountUnavailable, result.Error);
        Assert.Equal("authentication.account_unavailable", result.Error!.Code);
        Assert.Equal(ErrorType.Forbidden, result.Error.Type);
        Assert.Equal(0, _submissions.GetCallCount);
    }

    [Fact]
    public async Task AnActiveAccountProceedsToTheRepositoryForTheAuthenticatedUser()
    {
        var detail = Seed(Me);

        var result = await CreateHandler(Me, UserStatus.Active).Handle(new GetMyListingSubmissionByIdQuery(detail.SubmissionId));

        Assert.True(result.IsSuccess);
        Assert.Equal(detail.SubmissionId, result.Value.SubmissionId);
        Assert.Equal(1, _users.GetByIdCallCount);
        Assert.Equal(Me, _submissions.LastGetCall!.ActingUserId);
    }

    [Fact]
    public async Task TheAccountIsCheckedBeforeTheEmptyIdShortcut()
    {
        var inactive = await CreateHandler(Me, UserStatus.Suspended).Handle(new GetMyListingSubmissionByIdQuery(Guid.Empty));
        var missing = await CreateHandler(SomeoneElse, accountStatus: null).Handle(new GetMyListingSubmissionByIdQuery(Guid.Empty));

        // An inactive or unknown account learns nothing about submission ids, not even that the empty one is absent.
        Assert.Same(MeErrors.AccountUnavailable, inactive.Error);
        AssertUnauthorized(missing);
        Assert.Equal(0, _submissions.GetCallCount);
    }

    [Fact]
    public async Task NoAccountLookupIsMadeWithoutAUsableSubject()
    {
        var detail = Seed(Me);

        await CreateHandler(userId: null).Handle(new GetMyListingSubmissionByIdQuery(detail.SubmissionId));
        await CreateHandler(userId: Guid.Empty).Handle(new GetMyListingSubmissionByIdQuery(detail.SubmissionId));

        Assert.Equal(0, _users.GetByIdCallCount);
    }

    [Fact]
    public async Task TheAccountIsLookedUpForTheAuthenticatedUserWithTheCallersToken()
    {
        using var source = new CancellationTokenSource();
        var accounts = new RecordingUserRepository(UserStatus.Active);

        await CreateHandler(Me, users: accounts).Handle(new GetMyListingSubmissionByIdQuery(Guid.NewGuid()), source.Token);

        Assert.Equal(Me, accounts.LastRequestedId);
        Assert.Equal(source.Token, accounts.CancellationToken);
    }

    [Fact]
    public async Task AFailedAccountLookupPropagatesAndNeverBecomesAccess()
    {
        var detail = Seed(Me);
        var accounts = new RecordingUserRepository(UserStatus.Active) { ExceptionToThrow = new InvalidOperationException("identity unavailable") };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateHandler(Me, users: accounts).Handle(new GetMyListingSubmissionByIdQuery(detail.SubmissionId)));

        Assert.Equal("identity unavailable", exception.Message);
        Assert.Equal(0, _submissions.GetCallCount);
    }

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

    // By default the caller has an Active account, so the tests about submissions need not repeat it. Pass a status
    // to test another account state, null for no account at all, or a user repository of your own.
    private GetMyListingSubmissionByIdQueryHandler CreateHandler(
        Guid? userId,
        UserStatus? accountStatus = UserStatus.Active,
        IUserRepository? users = null)
    {
        if (users is null && userId is { } id && id != Guid.Empty && accountStatus is { } status)
        {
            SeedAccount(id, status);
        }

        return new GetMyListingSubmissionByIdQueryHandler(new FakeCurrentUser(userId), users ?? _users, _submissions);
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
