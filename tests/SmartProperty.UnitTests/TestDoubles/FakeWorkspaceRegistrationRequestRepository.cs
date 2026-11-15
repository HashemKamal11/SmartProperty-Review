using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.WorkspaceRegistrations;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>
/// In-memory workspace registration requests. Views are built from the stored request and the applicant in
/// <see cref="FakeUserRepository"/>, the way the real projection joins the two.
/// </summary>
internal sealed class FakeWorkspaceRegistrationRequestRepository(FakeUserRepository users)
    : IWorkspaceRegistrationRequestRepository
{
    private readonly Dictionary<Guid, WorkspaceRegistrationRequest> _byId = [];

    public List<WorkspaceRegistrationRequest> Added { get; } = [];

    public void Seed(WorkspaceRegistrationRequest request) => _byId[request.Id] = request;

    public Task<WorkspaceRegistrationRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_byId.GetValueOrDefault(id));

    public Task AddAsync(WorkspaceRegistrationRequest request, CancellationToken cancellationToken = default)
    {
        Added.Add(request);
        Seed(request);
        return Task.CompletedTask;
    }

    public Task<bool> HasPendingForApplicantAsync(Guid applicantUserId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_byId.Values.Any(request =>
            request.ApplicantUserId == applicantUserId && request.Status == WorkspaceRegistrationRequestStatus.Pending));

    public async Task<WorkspaceRegistrationView?> GetViewAsync(Guid id, CancellationToken cancellationToken = default) =>
        _byId.TryGetValue(id, out var request) ? await ViewAsync(request) : null;

    public async Task<WorkspaceRegistrationPage> ListAsync(
        WorkspaceRegistrationRequestStatus status,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        var matching = _byId.Values.Where(request => request.Status == status)
            .OrderBy(request => request.RequestedAt).ThenBy(request => request.Id).ToList();
        var items = new List<WorkspaceRegistrationView>();
        foreach (var request in matching.Skip(skip).Take(take))
            items.Add(await ViewAsync(request));
        return new WorkspaceRegistrationPage(items, matching.Count);
    }

    public async Task<IReadOnlyList<WorkspaceRegistrationView>> ListForApplicantAsync(
        Guid applicantUserId,
        CancellationToken cancellationToken = default)
    {
        var items = new List<WorkspaceRegistrationView>();
        foreach (var request in _byId.Values.Where(r => r.ApplicantUserId == applicantUserId).OrderByDescending(r => r.RequestedAt))
            items.Add(await ViewAsync(request));
        return items;
    }

    private async Task<WorkspaceRegistrationView> ViewAsync(WorkspaceRegistrationRequest request)
    {
        var user = (await users.GetByIdAsync(request.ApplicantUserId))!;
        return new WorkspaceRegistrationView(
            request.Id, request.CompanyName, request.CommercialRegistrationNumber, request.Status, request.RequestedAt,
            request.ReviewedAt, request.ReviewedByUserId, request.RejectionReason, request.WorkspaceId,
            user.Id, user.Email, user.FirstName, user.LastName, user.PhoneNumber, user.Status,
            user.IsContactVerificationComplete);
    }
}
