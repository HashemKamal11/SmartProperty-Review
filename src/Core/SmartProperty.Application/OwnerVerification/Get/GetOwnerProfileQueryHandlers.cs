using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Storage;
using SmartProperty.Common.Results;
using SmartProperty.Domain.OwnerVerification;

namespace SmartProperty.Application.OwnerVerification.Get;

/// <summary>The signed-in user's own owner profile, or an empty not-started one.</summary>
public sealed record GetMyOwnerProfileQuery(Guid UserId) : IQuery<OwnerProfileDetails>;

/// <summary>An owner profile for the reviewers. Profiles still in Draft are not visible to them.</summary>
public sealed record GetOwnerProfileForReviewQuery(Guid ProfileId) : IQuery<OwnerProfileDetails>;

/// <summary>The signed-in user's own address proof image.</summary>
public sealed record GetMyAddressProofQuery(Guid UserId) : IQuery<AddressProofFile>;

/// <summary>The address proof image of a profile, for the reviewers.</summary>
public sealed record GetAddressProofForReviewQuery(Guid ProfileId) : IQuery<AddressProofFile>;

public sealed class GetMyOwnerProfileQueryHandler(
    IUserRepository userRepository,
    IOwnerProfileRepository profileRepository)
    : IQueryHandler<GetMyOwnerProfileQuery, OwnerProfileDetails>
{
    public async Task<Result<OwnerProfileDetails>> Handle(
        GetMyOwnerProfileQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.UserId == Guid.Empty)
        {
            return Result<OwnerProfileDetails>.Failure(
                OwnerVerificationErrors.ValidationFailed("A signed-in user is required."));
        }

        var user = await userRepository.GetByIdAsync(query.UserId, cancellationToken);

        if (user is null)
        {
            return Result<OwnerProfileDetails>.Failure(OwnerVerificationErrors.UserNotFound);
        }

        var profile = await profileRepository.GetByUserIdAsync(query.UserId, cancellationToken);

        return Result<OwnerProfileDetails>.Success(OwnerProfileDetails.From(user, profile));
    }
}

public sealed class GetOwnerProfileForReviewQueryHandler(
    IUserRepository userRepository,
    IOwnerProfileRepository profileRepository)
    : IQueryHandler<GetOwnerProfileForReviewQuery, OwnerProfileDetails>
{
    public async Task<Result<OwnerProfileDetails>> Handle(
        GetOwnerProfileForReviewQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var profile = query.ProfileId == Guid.Empty
            ? null
            : await profileRepository.GetByIdAsync(query.ProfileId, cancellationToken);

        if (profile is null || profile.Status == OwnerVerificationStatus.Draft)
        {
            return Result<OwnerProfileDetails>.Failure(OwnerVerificationErrors.ProfileNotFound);
        }

        var user = await userRepository.GetByIdAsync(profile.UserId, cancellationToken);

        if (user is null)
        {
            return Result<OwnerProfileDetails>.Failure(OwnerVerificationErrors.UserNotFound);
        }

        return Result<OwnerProfileDetails>.Success(OwnerProfileDetails.From(user, profile));
    }
}

public sealed class GetMyAddressProofQueryHandler(
    IOwnerProfileRepository profileRepository,
    IImageStorage imageStorage)
    : IQueryHandler<GetMyAddressProofQuery, AddressProofFile>
{
    public async Task<Result<AddressProofFile>> Handle(
        GetMyAddressProofQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var profile = query.UserId == Guid.Empty
            ? null
            : await profileRepository.GetByUserIdAsync(query.UserId, cancellationToken);

        return await AddressProofReader.OpenAsync(profile, imageStorage, cancellationToken);
    }
}

public sealed class GetAddressProofForReviewQueryHandler(
    IOwnerProfileRepository profileRepository,
    IImageStorage imageStorage)
    : IQueryHandler<GetAddressProofForReviewQuery, AddressProofFile>
{
    public async Task<Result<AddressProofFile>> Handle(
        GetAddressProofForReviewQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var profile = query.ProfileId == Guid.Empty
            ? null
            : await profileRepository.GetByIdAsync(query.ProfileId, cancellationToken);

        // A reviewer sees only profiles that were submitted, never a draft still being written.
        if (profile?.Status == OwnerVerificationStatus.Draft)
        {
            profile = null;
        }

        return await AddressProofReader.OpenAsync(profile, imageStorage, cancellationToken);
    }
}

internal static class AddressProofReader
{
    public static async Task<Result<AddressProofFile>> OpenAsync(
        OwnerProfile? profile,
        IImageStorage imageStorage,
        CancellationToken cancellationToken)
    {
        if (profile is null)
        {
            return Result<AddressProofFile>.Failure(OwnerVerificationErrors.ProfileNotFound);
        }

        if (profile.AddressProofKey is null || profile.AddressProofContentType is null)
        {
            return Result<AddressProofFile>.Failure(OwnerVerificationErrors.ProofNotFound);
        }

        var stream = await imageStorage.OpenReadAsync(profile.AddressProofKey, cancellationToken);

        return stream is null
            ? Result<AddressProofFile>.Failure(OwnerVerificationErrors.ProofNotFound)
            : Result<AddressProofFile>.Success(
                new AddressProofFile(stream, profile.AddressProofContentType, profile.AddressProofFileName));
    }
}
