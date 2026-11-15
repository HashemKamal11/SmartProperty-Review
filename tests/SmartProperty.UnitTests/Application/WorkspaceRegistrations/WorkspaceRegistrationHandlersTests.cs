using SmartProperty.Application.Abstractions.Authentication;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.WorkspaceRegistrations.Apply;
using SmartProperty.Application.WorkspaceRegistrations.Register;
using SmartProperty.Application.WorkspaceRegistrations.Review;
using SmartProperty.Application.Workspaces;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.WorkspaceRegistrations;

public sealed class WorkspaceRegistrationHandlersTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid ReviewerId = Guid.NewGuid();

    private readonly FakeUserRepository _users = new();
    private readonly FakeUserCredentialRepository _credentials = new();
    private readonly FakeOneTimeCodeRepository _codes = new();
    private readonly FakeSmsSender _sms = new();
    private readonly FakeEmailSender _email = new();
    private readonly FakeWorkspaceRepository _workspaces = new();
    private readonly FakeWorkspaceMembershipRepository _memberships = new();
    private readonly FakeRoleRepository _roles = new();
    private readonly FakePermissionRepository _permissions = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeWorkspaceRegistrationRequestRepository _requests;

    public WorkspaceRegistrationHandlersTests()
    {
        _requests = new FakeWorkspaceRegistrationRequestRepository(_users);
    }

    // ---- Domain ----

    [Fact]
    public void ANewRequestIsPendingWithTrimmedValuesAndABlankRegistrationNumberIsAbsent()
    {
        var request = new WorkspaceRegistrationRequest(Guid.NewGuid(), Guid.NewGuid(), "  Acme Realty  ", "   ", Now);

        Assert.Equal(WorkspaceRegistrationRequestStatus.Pending, request.Status);
        Assert.Equal("Acme Realty", request.CompanyName);
        Assert.Null(request.CommercialRegistrationNumber);
        Assert.Null(request.WorkspaceId);
    }

    [Fact]
    public void ADecisionIsFinal()
    {
        var request = new WorkspaceRegistrationRequest(Guid.NewGuid(), Guid.NewGuid(), "Acme", "CR-1", Now);
        request.Reject(ReviewerId, " not a real company ", Now);

        Assert.Equal("not a real company", request.RejectionReason);
        Assert.Throws<InvalidOperationException>(() => request.Approve(ReviewerId, Guid.NewGuid(), Now));
        Assert.Throws<InvalidOperationException>(() => request.Reject(ReviewerId, "again", Now));
    }

    // ---- Company sign-up ----

    [Fact]
    public async Task SignUpCreatesAPendingAccountAndAPendingRequestInOneSaveAndSendsTheCodes()
    {
        var result = await SignUpHandler().Handle(SignUp());

        Assert.True(result.IsSuccess);
        Assert.Equal(UserStatus.Pending, result.Value.UserStatus);
        Assert.Equal(WorkspaceRegistrationRequestStatus.Pending, result.Value.RegistrationStatus);
        var request = Assert.Single(_requests.Added);
        Assert.Equal(result.Value.UserId, request.ApplicantUserId);
        Assert.Equal("Acme Realty", request.CompanyName);
        Assert.Equal("CR-123", request.CommercialRegistrationNumber);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
        Assert.Single(_sms.Sent);
        Assert.Single(_email.Sent);
        Assert.Empty(_roles.AddedRoles);
        Assert.Empty(_memberships.Added);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("Acme\nRealty")]
    public async Task SignUpWithAnInvalidCompanyNameSavesAndSendsNothing(string? companyName)
    {
        var result = await SignUpHandler().Handle(SignUp() with { CompanyName = companyName! });

        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
        Assert.Empty(_sms.Sent);
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task SignUpWithATakenEmailIsAConflictAndRecordsNoRequest()
    {
        _users.Seed(new User(Guid.NewGuid(), "owner@acme.test", "Old", "User", Now, "+201099999999"));

        var result = await SignUpHandler().Handle(SignUp());

        Assert.Equal("users.email_already_exists", result.Error!.Code);
        Assert.Empty(_requests.Added);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    // ---- An existing account applies ----

    [Fact]
    public async Task AnActiveAccountAppliesOnceAndASecondPendingRequestIsAConflict()
    {
        var user = VerifiedUser();
        var handler = ApplyHandler(user.Id);

        var first = await handler.Handle(new ApplyForWorkspaceCommand("Acme", null));
        var second = await handler.Handle(new ApplyForWorkspaceCommand("Acme 2", null));

        Assert.True(first.IsSuccess);
        Assert.Equal(WorkspaceRegistrationRequestStatus.Pending, first.Value.Status);
        Assert.Equal(user.Id, first.Value.ApplicantUserId);
        Assert.Equal("workspace_registrations.pending_request_exists", second.Error!.Code);
        Assert.Single(_requests.Added);
    }

    [Fact]
    public async Task APendingAccountCannotApply()
    {
        var user = new User(Guid.NewGuid(), "p@acme.test", "P", "User", Now, "+201011111111");
        _users.Seed(user);

        var result = await ApplyHandler(user.Id).Handle(new ApplyForWorkspaceCommand("Acme", null));

        Assert.Equal("workspace_registrations.account_not_active", result.Error!.Code);
        Assert.Empty(_requests.Added);
    }

    [Fact]
    public async Task ApplyingWithoutASignedInUserIsUnauthorized()
    {
        var result = await ApplyHandler(null).Handle(new ApplyForWorkspaceCommand("Acme", null));

        Assert.Equal("authentication.unauthorized", result.Error!.Code);
    }

    // ---- Approve ----

    [Fact]
    public async Task ApprovalCreatesTheWorkspaceAndMakesTheApplicantItsAdmin()
    {
        var applicant = VerifiedUser();
        var request = PendingRequest(applicant.Id);

        var result = await ApproveHandler().Handle(new ApproveWorkspaceRegistrationCommand(request.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkspaceRegistrationRequestStatus.Approved, request.Status);
        Assert.Equal(result.Value.WorkspaceId, request.WorkspaceId);
        Assert.Equal(ReviewerId, request.ReviewedByUserId);

        var workspace = await _workspaces.GetByIdAsync(result.Value.WorkspaceId);
        Assert.Equal("Acme Realty", workspace!.Name);

        var role = Assert.Single(_roles.AddedRoles);
        Assert.Equal(WorkspaceRoles.AdminRoleName, role.Name);
        Assert.Equal(RoleScope.Workspace, role.Scope);
        Assert.Equal(workspace.Id, role.WorkspaceId);
        Assert.Equal(
            new[] { PermissionCodes.WorkspaceMembersCreate, PermissionCodes.WorkspaceRead }.Order(),
            _permissions.Added.Select(permission => permission.Code).Order());

        var membership = Assert.Single(_memberships.Added);
        Assert.Equal(applicant.Id, membership.UserId);
        Assert.Equal(workspace.Id, membership.WorkspaceId);
        Assert.Equal(result.Value.MembershipId, membership.Id);
        var assignment = Assert.Single(_roles.AddedMembershipAssignments);
        Assert.Equal(role.Id, assignment.RoleId);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task ApprovalWaitsForTheApplicantToVerifyTheirContact()
    {
        var applicant = new User(Guid.NewGuid(), "u@acme.test", "U", "User", Now, "+201022222222");
        _users.Seed(applicant);
        var request = PendingRequest(applicant.Id);

        var result = await ApproveHandler().Handle(new ApproveWorkspaceRegistrationCommand(request.Id));

        Assert.Equal("workspace_registrations.contact_not_verified", result.Error!.Code);
        Assert.Equal(WorkspaceRegistrationRequestStatus.Pending, request.Status);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task ASuspendedApplicantIsRefusedNotRevived()
    {
        var applicant = VerifiedUser();
        applicant.Suspend(Now);
        var request = PendingRequest(applicant.Id);

        var result = await ApproveHandler().Handle(new ApproveWorkspaceRegistrationCommand(request.Id));

        Assert.Equal("workspace_registrations.applicant_unavailable", result.Error!.Code);
        Assert.Equal(UserStatus.Suspended, applicant.Status);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task ADecidedRequestCannotBeApprovedAndAnUnknownOneIsNotFound()
    {
        var request = PendingRequest(VerifiedUser().Id);
        request.Reject(ReviewerId, "no", Now);

        Assert.Equal(
            "workspace_registrations.already_reviewed",
            (await ApproveHandler().Handle(new ApproveWorkspaceRegistrationCommand(request.Id))).Error!.Code);
        Assert.Equal(
            "workspace_registrations.not_found",
            (await ApproveHandler().Handle(new ApproveWorkspaceRegistrationCommand(Guid.NewGuid()))).Error!.Code);
    }

    [Fact]
    public async Task AConcurrentDecisionIsReportedAsAlreadyReviewed()
    {
        var request = PendingRequest(VerifiedUser().Id);
        _unitOfWork.ExceptionToThrow = new ConcurrencyConflictException(
            PersistenceResource.WorkspaceRegistrationRequest, new InvalidOperationException("simulated"));

        var result = await ApproveHandler().Handle(new ApproveWorkspaceRegistrationCommand(request.Id));

        Assert.Equal("workspace_registrations.already_reviewed", result.Error!.Code);
    }

    // ---- Reject ----

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task RejectionNeedsAReason(string? reason)
    {
        var request = PendingRequest(VerifiedUser().Id);

        var result = await RejectHandler().Handle(new RejectWorkspaceRegistrationCommand(request.Id, reason));

        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Equal(WorkspaceRegistrationRequestStatus.Pending, request.Status);
    }

    [Fact]
    public async Task RejectionRecordsTheReasonAndCreatesNothing()
    {
        var request = PendingRequest(VerifiedUser().Id);

        var result = await RejectHandler().Handle(new RejectWorkspaceRegistrationCommand(request.Id, "Missing CR"));

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkspaceRegistrationRequestStatus.Rejected, result.Value.Status);
        Assert.Equal("Missing CR", result.Value.RejectionReason);
        Assert.Null(result.Value.WorkspaceId);
        Assert.Empty(_roles.AddedRoles);
        Assert.Empty(_memberships.Added);
    }

    // ---- Helpers ----

    private static RegisterWorkspaceCommand SignUp() =>
        new("owner@acme.test", "a-password", "Sara", "Owner", "+201012345678", "  Acme Realty ", "CR-123");

    private User VerifiedUser()
    {
        var user = new User(Guid.NewGuid(), $"{Guid.NewGuid():N}@acme.test", "Sara", "Owner", Now, "+2010" + Random.Shared.Next(10000000, 99999999));
        user.VerifyPhone(Now);
        user.VerifyEmail(Now);
        user.ActivateWhenContactVerified(Now);
        _users.Seed(user);
        return user;
    }

    private WorkspaceRegistrationRequest PendingRequest(Guid applicantId)
    {
        var request = new WorkspaceRegistrationRequest(Guid.NewGuid(), applicantId, "Acme Realty", "CR-1", Now);
        _requests.Seed(request);
        return request;
    }

    private RegisterWorkspaceCommandHandler SignUpHandler() =>
        new(_users, _credentials, _codes, _requests, new FakePasswordHasher(), _sms, _email,
            new StubPolicy(), new FakeDateTimeProvider(Now), _unitOfWork);

    private ApplyForWorkspaceCommandHandler ApplyHandler(Guid? userId) =>
        new(new FakeCurrentUser(userId), _users, _requests, new FakeDateTimeProvider(Now), _unitOfWork);

    private ApproveWorkspaceRegistrationCommandHandler ApproveHandler() =>
        new(new FakeCurrentUser(ReviewerId), _requests, _users, _workspaces, _memberships, _roles, _permissions,
            new FakeDateTimeProvider(Now), _unitOfWork);

    private RejectWorkspaceRegistrationCommandHandler RejectHandler() =>
        new(new FakeCurrentUser(ReviewerId), _requests, new FakeDateTimeProvider(Now), _unitOfWork);

    private sealed class StubPolicy : IContactVerificationPolicy
    {
        public bool AutoVerifyPhone => false;
    }
}
