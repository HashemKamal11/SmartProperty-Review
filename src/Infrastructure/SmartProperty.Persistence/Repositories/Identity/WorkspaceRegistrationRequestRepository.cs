using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.WorkspaceRegistrations;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Repositories.Identity;

internal sealed class WorkspaceRegistrationRequestRepository(ApplicationDbContext dbContext)
    : IWorkspaceRegistrationRequestRepository
{
    public Task<WorkspaceRegistrationRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.WorkspaceRegistrationRequests.FirstOrDefaultAsync(request => request.Id == id, cancellationToken);

    public async Task AddAsync(WorkspaceRegistrationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await dbContext.WorkspaceRegistrationRequests.AddAsync(request, cancellationToken);
    }

    public Task<bool> HasPendingForApplicantAsync(Guid applicantUserId, CancellationToken cancellationToken = default) =>
        dbContext.WorkspaceRegistrationRequests.AnyAsync(
            request => request.ApplicantUserId == applicantUserId
                && request.Status == WorkspaceRegistrationRequestStatus.Pending,
            cancellationToken);

    public Task<WorkspaceRegistrationView?> GetViewAsync(Guid id, CancellationToken cancellationToken = default) =>
        Rows()
            .Where(row => row.Request.Id == id)
            .Select(ToView)
            .FirstOrDefaultAsync(cancellationToken);

    /// <remarks>Oldest first, tie-broken by id so paging is stable.</remarks>
    public async Task<WorkspaceRegistrationPage> ListAsync(
        WorkspaceRegistrationRequestStatus status,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (skip is < 0 or > PageParameters.MaxOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), skip, $"Skip must be between 0 and {PageParameters.MaxOffset}.");
        }

        if (take is < 1 or > PageParameters.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, $"Take must be between 1 and {PageParameters.MaxPageSize}.");
        }

        var totalCount = await dbContext.WorkspaceRegistrationRequests
            .LongCountAsync(request => request.Status == status, cancellationToken);

        var items = await Rows()
            .Where(row => row.Request.Status == status)
            .OrderBy(row => row.Request.RequestedAt)
            .ThenBy(row => row.Request.Id)
            .Skip(skip)
            .Take(take)
            .Select(ToView)
            .ToListAsync(cancellationToken);

        return new WorkspaceRegistrationPage(items, totalCount);
    }

    public async Task<IReadOnlyList<WorkspaceRegistrationView>> ListForApplicantAsync(
        Guid applicantUserId,
        CancellationToken cancellationToken = default) =>
        await Rows()
            .Where(row => row.Request.ApplicantUserId == applicantUserId)
            .OrderByDescending(row => row.Request.RequestedAt)
            .ThenBy(row => row.Request.Id)
            .Select(ToView)
            .ToListAsync(cancellationToken);

    // The applicant comes from the same query; the projection below runs in the database.
    private IQueryable<Row> Rows() =>
        dbContext.WorkspaceRegistrationRequests.Join(
            dbContext.Users,
            request => request.ApplicantUserId,
            user => user.Id,
            (request, user) => new Row { Request = request, User = user });

    private static readonly System.Linq.Expressions.Expression<Func<Row, WorkspaceRegistrationView>> ToView = row =>
        new WorkspaceRegistrationView(
            row.Request.Id,
            row.Request.CompanyName,
            row.Request.CommercialRegistrationNumber,
            row.Request.Status,
            row.Request.RequestedAt,
            row.Request.ReviewedAt,
            row.Request.ReviewedByUserId,
            row.Request.RejectionReason,
            row.Request.WorkspaceId,
            row.User.Id,
            row.User.Email,
            row.User.FirstName,
            row.User.LastName,
            row.User.PhoneNumber,
            row.User.Status,
            // Mirrors User.IsContactVerificationComplete, which is not mapped and so cannot be translated.
            row.User.PhoneNumber == null || (row.User.PhoneVerifiedAt != null && row.User.EmailVerifiedAt != null));

    private sealed class Row
    {
        public required WorkspaceRegistrationRequest Request { get; init; }
        public required User User { get; init; }
    }
}
