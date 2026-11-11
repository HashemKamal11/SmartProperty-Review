using SmartProperty.Application.Authorization;
using SmartProperty.Application.OwnerVerification;
using SmartProperty.Application.OwnerVerification.Get;
using SmartProperty.Application.OwnerVerification.Review;
using SmartProperty.Application.OwnerVerification.Save;
using SmartProperty.Application.OwnerVerification.Submit;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.OwnerVerification;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.OwnerVerification;

public sealed class OwnerVerificationHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 1, 2, 3, 4];

    private readonly FakeUserRepository _users = new();
    private readonly FakeOwnerProfileRepository _profiles = new();
    private readonly FakePermissionRepository _permissions = new();
    private readonly FakeRoleRepository _roles = new();
    private readonly FakeImageStorage _storage = new();
    private readonly FakeDateTimeProvider _clock = new(Now);
    private readonly FakeUnitOfWork _unitOfWork = new();

    private readonly User _owner;
    private readonly User _reviewer;

    public OwnerVerificationHandlerTests()
    {
        _owner = new User(Guid.NewGuid(), "owner@example.com", "Abdullah", "Salem", Now);
        _reviewer = new User(Guid.NewGuid(), "reviewer@example.com", "Rana", "Admin", Now);
        _users.Seed(_owner);
        _users.Seed(_reviewer);
    }

    // ---- identity step

    [Fact]
    public async Task SavingTheIdentityStartsTheProfileAsADraft()
    {
        var result = await SaveIdentityAsync(_owner.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal("Draft", result.Value.Status);
        Assert.False(result.Value.CanAddProperties);
        Assert.Equal("1234567890", result.Value.Identity!.NationalId);
        Assert.Equal("owner@example.com", result.Value.Account.Email);
        Assert.Single(_profiles.Added);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task InvalidIdentityIsAValidationFailureAndSavesNothing()
    {
        var result = await SaveIdentityAsync(_owner.Id, nationalId: "123");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        Assert.DoesNotContain("(Parameter", result.Error.Description);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(1, _unitOfWork.DiscardTrackedChangesCallCount);
    }

    [Fact]
    public async Task MissingDatesAreAValidationFailure()
    {
        var handler = IdentityHandler();

        var result = await handler.Handle(
            new SaveOwnerIdentityCommand(_owner.Id, "SA", "1234567890", "Saudi", null, null, true));

        Assert.Equal(ErrorType.Validation, result.Error!.Type);
    }

    [Fact]
    public async Task ANationalIdAlreadyHeldByAnotherAccountIsAConflict()
    {
        await SaveIdentityAsync(_reviewer.Id);

        var result = await SaveIdentityAsync(_owner.Id);

        Assert.Equal("owner_profile.national_id_taken", result.Error!.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
    }

    [Fact]
    public async Task TheSameIdWrittenWithHyphensIsStillAConflict()
    {
        await SaveIdentityAsync(_reviewer.Id, nationalId: "784199012345671", country: "AE");

        var result = await SaveIdentityAsync(_owner.Id, nationalId: "784-1990-1234567-1", country: "ae");

        Assert.Equal("owner_profile.national_id_taken", result.Error!.Code);
    }

    [Fact]
    public async Task AnUnsupportedIdCountryIsAValidationFailure()
    {
        var result = await SaveIdentityAsync(_owner.Id, country: "US");

        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task TheSameUserMaySaveTheirOwnIdAgain()
    {
        await SaveIdentityAsync(_owner.Id);

        var again = await SaveIdentityAsync(_owner.Id, nationality: "Egyptian");

        Assert.True(again.IsSuccess);
        Assert.Equal("Egyptian", again.Value.Identity!.Nationality);
        Assert.Single(_profiles.Stored);
    }

    [Fact]
    public async Task AnUnknownUserIsNotFound()
    {
        var result = await SaveIdentityAsync(Guid.NewGuid());

        Assert.Equal(ErrorType.NotFound, result.Error!.Type);
    }

    [Fact]
    public async Task AProfileUnderReviewCannotBeEdited()
    {
        await CompleteAndSubmitAsync(_owner.Id);

        var result = await SaveIdentityAsync(_owner.Id, nationality: "Egyptian");

        Assert.Equal("owner_profile.locked_for_review", result.Error!.Code);
    }

    // ---- address step and proof

    [Fact]
    public async Task SavingTheAddressStoresItAndNeedsNoProofYet()
    {
        var result = await AddressHandler().Handle(AddressCommand(_owner.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal("1234", result.Value.Address!.BuildingNumber);
        Assert.False(result.Value.Address.HasProofImage);
    }

    [Fact]
    public async Task ABadPostalCodeIsAValidationFailure()
    {
        var result = await AddressHandler().Handle(AddressCommand(_owner.Id, postalCode: "12"));

        Assert.Equal(ErrorType.Validation, result.Error!.Type);
    }

    [Fact]
    public async Task UploadingTheProofStoresTheImageUnderTheProfile()
    {
        var result = await UploadProofAsync(_owner.Id);

        Assert.True(result.IsSuccess);
        var key = Assert.Single(_storage.Saved);
        Assert.StartsWith("owner-profiles/", key);
        Assert.EndsWith(".png", key);
        Assert.Equal(_profiles.Stored.Single().AddressProofKey, key);
        Assert.True(_storage.Files.ContainsKey(key));
    }

    [Fact]
    public async Task ReplacingTheProofDeletesTheEarlierImage()
    {
        await UploadProofAsync(_owner.Id);
        var first = _storage.Saved.Single();

        await UploadProofAsync(_owner.Id);

        Assert.Equal(2, _storage.Saved.Count);
        Assert.Equal([first], _storage.Deleted);
        Assert.Equal(_storage.Saved[1], _profiles.Stored.Single().AddressProofKey);
    }

    [Fact]
    public async Task AFileThatIsNotAnImageIsRefusedAndNothingIsStored()
    {
        var result = await UploadProofAsync(_owner.Id, bytes: "%PDF-1.7 not an image at all"u8.ToArray());

        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        Assert.Empty(_storage.Saved);
        Assert.Empty(_profiles.Stored);
    }

    [Fact]
    public async Task AnEmptyOrOversizedFileIsRefused()
    {
        var empty = await UploadProofAsync(_owner.Id, bytes: []);
        var big = await UploadProofAsync(
            _owner.Id,
            lengthOverride: UploadAddressProofCommandHandler.MaxSizeBytes + 1);

        Assert.Equal(ErrorType.Validation, empty.Error!.Type);
        Assert.Equal(ErrorType.Validation, big.Error!.Type);
        Assert.Empty(_storage.Saved);
    }

    [Fact]
    public async Task AProofThatCannotBeRecordedIsDeletedAgain()
    {
        _unitOfWork.ExceptionToThrow = new InvalidOperationException("database down");

        await Assert.ThrowsAsync<InvalidOperationException>(() => UploadProofAsync(_owner.Id));

        Assert.Equal(_storage.Saved, _storage.Deleted);
    }

    // ---- submit

    [Fact]
    public async Task SubmittingWithoutTheIdentityIsAValidationFailure()
    {
        var result = await SubmitAsync(_owner.Id);

        Assert.Equal("owner_profile.identity_incomplete", result.Error!.Code);
    }

    [Fact]
    public async Task SubmittingWithoutTheProofIsAValidationFailure()
    {
        await SaveIdentityAsync(_owner.Id);
        await AddressHandler().Handle(AddressCommand(_owner.Id));

        var result = await SubmitAsync(_owner.Id);

        Assert.Equal("owner_profile.address_incomplete", result.Error!.Code);
    }

    [Fact]
    public async Task ACompleteProfileMovesUnderReview()
    {
        var result = await CompleteAndSubmitAsync(_owner.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal("UnderReview", result.Value.Status);
        Assert.Equal(Now, result.Value.SubmittedAt);
    }

    [Fact]
    public async Task SubmittingTwiceIsAConflict()
    {
        await CompleteAndSubmitAsync(_owner.Id);

        var again = await SubmitAsync(_owner.Id);

        Assert.Equal(ErrorType.Conflict, again.Error!.Type);
    }

    // ---- review

    [Fact]
    public async Task ApprovingGrantsThePropertyOwnerRoleWithOnlyCreateAndRead()
    {
        var submitted = await CompleteAndSubmitAsync(_owner.Id);

        var result = await ApproveAsync(submitted.Value.ProfileId!.Value);

        Assert.True(result.IsSuccess);
        Assert.Equal("Approved", result.Value.Status);
        Assert.True(result.Value.CanAddProperties);

        var role = Assert.Single(_roles.AddedRoles);
        Assert.Equal(OwnerAccessProvisioner.PropertyOwnerRoleName, role.Name);
        Assert.Equal(RoleScope.Platform, role.Scope);
        Assert.Null(role.WorkspaceId);
        Assert.Equal(
            [PermissionCodes.PropertyCreate, PermissionCodes.PropertyRead],
            _permissions.Added.Select(permission => permission.Code).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(2, _roles.AddedRolePermissions.Count);
        var assignment = Assert.Single(_roles.AddedUserAssignments);
        Assert.Equal(_owner.Id, assignment.UserId);
        Assert.Equal(role.Id, assignment.RoleId);
    }

    [Fact]
    public async Task ASecondOwnerReusesTheRoleAndThePermissions()
    {
        var first = await CompleteAndSubmitAsync(_owner.Id);
        await ApproveAsync(first.Value.ProfileId!.Value);
        var other = new User(Guid.NewGuid(), "second@example.com", "Second", "Owner", Now);
        _users.Seed(other);
        var second = await CompleteAndSubmitAsync(other.Id, nationalId: "2234567890");

        await ApproveAsync(second.Value.ProfileId!.Value);

        Assert.Single(_roles.AddedRoles);
        Assert.Equal(2, _permissions.Added.Count);
        Assert.Equal(2, _roles.AddedUserAssignments.Count);
    }

    [Fact]
    public async Task ApprovingNeverGrantsUpdateArchiveOrReview()
    {
        var submitted = await CompleteAndSubmitAsync(_owner.Id);

        await ApproveAsync(submitted.Value.ProfileId!.Value);

        var codes = _permissions.Added.Select(permission => permission.Code).ToArray();
        Assert.DoesNotContain(PermissionCodes.PropertyUpdate, codes);
        Assert.DoesNotContain(PermissionCodes.PropertyArchive, codes);
        Assert.DoesNotContain(PermissionCodes.OwnerVerificationReview, codes);
    }

    [Fact]
    public async Task ADraftIsInvisibleToReviewers()
    {
        await SaveIdentityAsync(_owner.Id);
        var draftId = _profiles.Stored.Single().Id;

        var approve = await ApproveAsync(draftId);
        var reject = await RejectAsync(draftId, "no");
        var detail = await new GetOwnerProfileForReviewQueryHandler(_users, _profiles)
            .Handle(new GetOwnerProfileForReviewQuery(draftId));

        Assert.Equal(ErrorType.NotFound, approve.Error!.Type);
        Assert.Equal(ErrorType.NotFound, reject.Error!.Type);
        Assert.Equal(ErrorType.NotFound, detail.Error!.Type);
    }

    [Fact]
    public async Task OnlyAProfileUnderReviewCanBeDecided()
    {
        var submitted = await CompleteAndSubmitAsync(_owner.Id);
        var id = submitted.Value.ProfileId!.Value;
        await ApproveAsync(id);

        var approveAgain = await ApproveAsync(id);
        var rejectAfter = await RejectAsync(id, "changed my mind");

        Assert.Equal("owner_profile.not_under_review", approveAgain.Error!.Code);
        Assert.Equal("owner_profile.not_under_review", rejectAfter.Error!.Code);
    }

    [Fact]
    public async Task AReviewerCannotDecideTheirOwnProfile()
    {
        var submitted = await CompleteAndSubmitAsync(_reviewer.Id);
        var id = submitted.Value.ProfileId!.Value;

        var approve = await ApproveAsync(id);
        var reject = await RejectAsync(id, "no");

        Assert.Equal(ErrorType.Forbidden, approve.Error!.Type);
        Assert.Equal(ErrorType.Forbidden, reject.Error!.Type);
        Assert.Empty(_roles.AddedUserAssignments);
    }

    [Fact]
    public async Task RejectingNeedsAReasonAndGrantsNothing()
    {
        var submitted = await CompleteAndSubmitAsync(_owner.Id);
        var id = submitted.Value.ProfileId!.Value;

        var blank = await RejectAsync(id, "   ");
        var result = await RejectAsync(id, "The address proof is blurry.");

        Assert.Equal(ErrorType.Validation, blank.Error!.Type);
        Assert.Equal("Rejected", result.Value.Status);
        Assert.Equal("The address proof is blurry.", result.Value.RejectionReason);
        Assert.False(result.Value.CanAddProperties);
        Assert.Empty(_roles.AddedUserAssignments);
    }

    [Fact]
    public async Task ARejectedOwnerCorrectsAndResubmits()
    {
        var submitted = await CompleteAndSubmitAsync(_owner.Id);
        await RejectAsync(submitted.Value.ProfileId!.Value, "Wrong street.");

        var edit = await AddressHandler().Handle(AddressCommand(_owner.Id, street: "Right street"));
        var resubmit = await SubmitAsync(_owner.Id);

        Assert.Equal("Draft", edit.Value.Status);
        Assert.Null(edit.Value.RejectionReason);
        Assert.Equal("UnderReview", resubmit.Value.Status);
    }

    [Fact]
    public async Task EditingAnApprovedProfileWithdrawsTheGrant()
    {
        var submitted = await CompleteAndSubmitAsync(_owner.Id);
        await ApproveAsync(submitted.Value.ProfileId!.Value);
        Assert.Single(_roles.UserAssignments);

        var edit = await SaveIdentityAsync(_owner.Id, nationality: "Egyptian");

        Assert.Equal("Draft", edit.Value.Status);
        Assert.False(edit.Value.CanAddProperties);
        Assert.Empty(_roles.UserAssignments);
    }

    // ---- queue and proof reads

    [Fact]
    public async Task TheQueueListsSubmittedProfilesOnly()
    {
        await SaveIdentityAsync(_reviewer.Id, nationalId: "1999999999");
        await CompleteAndSubmitAsync(_owner.Id);
        var handler = new GetOwnerReviewQueueQueryHandler(_profiles);

        var all = await handler.Handle(new GetOwnerReviewQueueQuery(null, null, null));
        var approved = await handler.Handle(new GetOwnerReviewQueueQuery(OwnerVerificationStatus.Approved, null, null));

        Assert.Equal(1, all.Value.TotalCount);
        Assert.Equal(_owner.Id, all.Value.Items.Single().UserId);
        Assert.Equal(0, approved.Value.TotalCount);
    }

    [Fact]
    public async Task TheQueueRefusesTheDraftStatusAndUnknownValues()
    {
        var handler = new GetOwnerReviewQueueQueryHandler(_profiles);

        var draft = await handler.Handle(new GetOwnerReviewQueueQuery(OwnerVerificationStatus.Draft, null, null));
        var unknown = await handler.Handle(new GetOwnerReviewQueueQuery((OwnerVerificationStatus)99, null, null));

        Assert.Equal(ErrorType.Validation, draft.Error!.Type);
        Assert.Equal(ErrorType.Validation, unknown.Error!.Type);
    }

    [Fact]
    public async Task AUserWhoHasNotStartedGetsAnEmptyNotStartedProfile()
    {
        var result = await new GetMyOwnerProfileQueryHandler(_users, _profiles)
            .Handle(new GetMyOwnerProfileQuery(_owner.Id));

        Assert.Equal(OwnerProfileDetails.NotStartedStatus, result.Value.Status);
        Assert.Null(result.Value.ProfileId);
        Assert.Null(result.Value.Identity);
        Assert.Null(result.Value.Address);
        Assert.Equal("Abdullah", result.Value.Account.FirstName);
    }

    [Fact]
    public async Task TheOwnerAndTheReviewerCanReadTheStoredProof()
    {
        var submitted = await CompleteAndSubmitAsync(_owner.Id);

        var mine = await new GetMyAddressProofQueryHandler(_profiles, _storage)
            .Handle(new GetMyAddressProofQuery(_owner.Id));
        var reviewed = await new GetAddressProofForReviewQueryHandler(_profiles, _storage)
            .Handle(new GetAddressProofForReviewQuery(submitted.Value.ProfileId!.Value));

        Assert.Equal("image/png", mine.Value.ContentType);
        Assert.Equal("image/png", reviewed.Value.ContentType);
    }

    [Fact]
    public async Task NoProofAndAnotherUsersProofAreNotFound()
    {
        var none = await new GetMyAddressProofQueryHandler(_profiles, _storage)
            .Handle(new GetMyAddressProofQuery(_owner.Id));
        await CompleteAndSubmitAsync(_reviewer.Id);
        var stillNone = await new GetMyAddressProofQueryHandler(_profiles, _storage)
            .Handle(new GetMyAddressProofQuery(_owner.Id));

        Assert.Equal(ErrorType.NotFound, none.Error!.Type);
        Assert.Equal(ErrorType.NotFound, stillNone.Error!.Type);
    }

    // ---- helpers

    private SaveOwnerIdentityCommandHandler IdentityHandler() =>
        new(_users, _profiles, Provisioner(), _clock, _unitOfWork);

    private SaveOwnerAddressCommandHandler AddressHandler() =>
        new(_users, _profiles, Provisioner(), _clock, _unitOfWork);

    private OwnerAccessProvisioner Provisioner() => new(_permissions, _roles, _clock);

    private Task<Result<OwnerProfileDetails>> SaveIdentityAsync(
        Guid userId,
        string nationalId = "1234567890",
        string nationality = "Saudi",
        string country = "SA")
    {
        return IdentityHandler().Handle(new SaveOwnerIdentityCommand(
            userId,
            country,
            nationalId,
            nationality,
            new DateOnly(1990, 1, 1),
            new DateOnly(2032, 1, 1),
            true));
    }

    private static SaveOwnerAddressCommand AddressCommand(
        Guid userId,
        string street = "Al Olaya",
        string postalCode = "12345")
    {
        return new SaveOwnerAddressCommand(userId, "1234", street, "Al Malqa", "Riyadh", postalCode, "5678", true);
    }

    private Task<Result<OwnerProfileDetails>> UploadProofAsync(
        Guid userId,
        byte[]? bytes = null,
        long? lengthOverride = null)
    {
        bytes ??= PngBytes;
        var handler = new UploadAddressProofCommandHandler(_users, _profiles, Provisioner(), _storage, _clock, _unitOfWork);

        return handler.Handle(new UploadAddressProofCommand(
            userId,
            "card.png",
            new MemoryStream(bytes),
            lengthOverride ?? bytes.Length));
    }

    private Task<Result<OwnerProfileDetails>> SubmitAsync(Guid userId)
    {
        return new SubmitOwnerProfileCommandHandler(_users, _profiles, _clock, _unitOfWork)
            .Handle(new SubmitOwnerProfileCommand(userId));
    }

    private async Task<Result<OwnerProfileDetails>> CompleteAndSubmitAsync(Guid userId, string nationalId = "1234567890")
    {
        await SaveIdentityAsync(userId, nationalId);
        await AddressHandler().Handle(AddressCommand(userId));
        await UploadProofAsync(userId);

        return await SubmitAsync(userId);
    }

    private Task<Result<OwnerProfileDetails>> ApproveAsync(Guid profileId)
    {
        return new ApproveOwnerProfileCommandHandler(_users, _profiles, Provisioner(), _clock, _unitOfWork)
            .Handle(new ApproveOwnerProfileCommand(profileId, _reviewer.Id));
    }

    private Task<Result<OwnerProfileDetails>> RejectAsync(Guid profileId, string? reason)
    {
        return new RejectOwnerProfileCommandHandler(_users, _profiles, _clock, _unitOfWork)
            .Handle(new RejectOwnerProfileCommand(profileId, _reviewer.Id, reason));
    }
}
