using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Pagination;
using SmartProperty.Common.Results;
using SmartProperty.Domain.OwnerVerification;

namespace SmartProperty.Application.OwnerVerification.Review;

/// <summary>One page of the reviewers' queue. Without a status it lists everything that has been submitted.</summary>
public sealed record GetOwnerReviewQueueQuery(OwnerVerificationStatus? Status, int? Page, int? PageSize)
    : IQuery<PagedList<OwnerReviewItem>>;

/// <summary>Approves a profile under review and grants its owner the right to add properties.</summary>
public sealed record ApproveOwnerProfileCommand(Guid ProfileId, Guid ReviewerUserId) : ICommand<OwnerProfileDetails>;

/// <summary>Rejects a profile under review with a reason the owner will see.</summary>
public sealed record RejectOwnerProfileCommand(Guid ProfileId, Guid ReviewerUserId, string? Reason)
    : ICommand<OwnerProfileDetails>;

public sealed class GetOwnerReviewQueueQueryHandler(IOwnerProfileRepository profileRepository)
    : IQueryHandler<GetOwnerReviewQueueQuery, PagedList<OwnerReviewItem>>
{
    public async Task<Result<PagedList<OwnerReviewItem>>> Handle(
        GetOwnerReviewQueueQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Status is { } status && (!Enum.IsDefined(status) || status == OwnerVerificationStatus.Draft))
        {
            return Result<PagedList<OwnerReviewItem>>.Failure(
                OwnerVerificationErrors.ValidationFailed("The status must be UnderReview, Approved or Rejected."));
        }

        var pageParameters = new PageParameters();

        if (query.Page is { } page)
        {
            pageParameters.PageNumber = page;
        }

        if (query.PageSize is { } pageSize)
        {
            pageParameters.PageSize = pageSize;
        }

        var result = await profileRepository.ListForReviewAsync(
            query.Status,
            pageParameters.Offset,
            pageParameters.PageSize,
            cancellationToken);

        return Result<PagedList<OwnerReviewItem>>.Success(
            PagedList<OwnerReviewItem>.Create(
                result.Items,
                pageParameters.PageNumber,
                pageParameters.PageSize,
                result.TotalCount));
    }
}

public sealed class ApproveOwnerProfileCommandHandler(
    IUserRepository userRepository,
    IOwnerProfileRepository profileRepository,
    OwnerAccessProvisioner accessProvisioner,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<ApproveOwnerProfileCommand, OwnerProfileDetails>
{
    public async Task<Result<OwnerProfileDetails>> Handle(
        ApproveOwnerProfileCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.ReviewerUserId == Guid.Empty)
        {
            return Result<OwnerProfileDetails>.Failure(
                OwnerVerificationErrors.ValidationFailed("A signed-in reviewer is required."));
        }

        // The first approval ever creates the role and its permissions; two of those at once can lose the unique
        // constraints, and the second attempt then finds what the first created.
        for (var attempt = 0; ; attempt++)
        {
            var profile = command.ProfileId == Guid.Empty
                ? null
                : await profileRepository.GetByIdAsync(command.ProfileId, cancellationToken);

            if (profile is null || profile.Status == OwnerVerificationStatus.Draft)
            {
                return Result<OwnerProfileDetails>.Failure(OwnerVerificationErrors.ProfileNotFound);
            }

            if (profile.Status != OwnerVerificationStatus.UnderReview)
            {
                return Result<OwnerProfileDetails>.Failure(OwnerVerificationErrors.NotUnderReview);
            }

            if (profile.UserId == command.ReviewerUserId)
            {
                return Result<OwnerProfileDetails>.Failure(OwnerVerificationErrors.CannotReviewOwnProfile);
            }

            var user = await userRepository.GetByIdAsync(profile.UserId, cancellationToken);

            if (user is null)
            {
                return Result<OwnerProfileDetails>.Failure(OwnerVerificationErrors.UserNotFound);
            }

            profile.Approve(command.ReviewerUserId, dateTimeProvider.UtcNow);
            await accessProvisioner.GrantAsync(profile.UserId, cancellationToken);

            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (UniqueConstraintViolationException) when (attempt == 0)
            {
                unitOfWork.DiscardTrackedChanges();

                continue;
            }

            return Result<OwnerProfileDetails>.Success(OwnerProfileDetails.From(user, profile));
        }
    }
}

public sealed class RejectOwnerProfileCommandHandler(
    IUserRepository userRepository,
    IOwnerProfileRepository profileRepository,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<RejectOwnerProfileCommand, OwnerProfileDetails>
{
    public async Task<Result<OwnerProfileDetails>> Handle(
        RejectOwnerProfileCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.ReviewerUserId == Guid.Empty)
        {
            return Result<OwnerProfileDetails>.Failure(
                OwnerVerificationErrors.ValidationFailed("A signed-in reviewer is required."));
        }

        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            return Result<OwnerProfileDetails>.Failure(
                OwnerVerificationErrors.ValidationFailed("A rejection reason is required."));
        }

        if (command.Reason.Trim().Length > OwnerProfile.MaxRejectionReasonLength)
        {
            return Result<OwnerProfileDetails>.Failure(OwnerVerificationErrors.ValidationFailed(
                $"The reason must be at most {OwnerProfile.MaxRejectionReasonLength} characters."));
        }

        var profile = command.ProfileId == Guid.Empty
            ? null
            : await profileRepository.GetByIdAsync(command.ProfileId, cancellationToken);

        if (profile is null || profile.Status == OwnerVerificationStatus.Draft)
        {
            return Result<OwnerProfileDetails>.Failure(OwnerVerificationErrors.ProfileNotFound);
        }

        if (profile.Status != OwnerVerificationStatus.UnderReview)
        {
            return Result<OwnerProfileDetails>.Failure(OwnerVerificationErrors.NotUnderReview);
        }

        if (profile.UserId == command.ReviewerUserId)
        {
            return Result<OwnerProfileDetails>.Failure(OwnerVerificationErrors.CannotReviewOwnProfile);
        }

        var user = await userRepository.GetByIdAsync(profile.UserId, cancellationToken);

        if (user is null)
        {
            return Result<OwnerProfileDetails>.Failure(OwnerVerificationErrors.UserNotFound);
        }

        profile.Reject(command.ReviewerUserId, command.Reason, dateTimeProvider.UtcNow);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<OwnerProfileDetails>.Success(OwnerProfileDetails.From(user, profile));
    }
}
