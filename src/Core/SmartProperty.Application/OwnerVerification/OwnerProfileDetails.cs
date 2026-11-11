using SmartProperty.Domain.Identity;
using SmartProperty.Domain.OwnerVerification;

namespace SmartProperty.Application.OwnerVerification;

/// <summary>
/// The three-step owner profile as one read model. Step 1 (account) is always read from the user; steps 2 and 3 are
/// null until saved.
/// </summary>
/// <param name="ProfileId">Null while the user has not started the profile.</param>
/// <param name="Status">The stored status name, or <see cref="NotStartedStatus"/> before the first save.</param>
/// <param name="CanAddProperties">True only when the profile is approved.</param>
public sealed record OwnerProfileDetails(
    Guid? ProfileId,
    Guid UserId,
    string Status,
    bool CanAddProperties,
    OwnerAccountStep Account,
    OwnerIdentityStep? Identity,
    OwnerAddressStep? Address,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? ReviewedAt,
    string? RejectionReason)
{
    public const string NotStartedStatus = "NotStarted";

    internal static OwnerProfileDetails From(User user, OwnerProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(user);

        var account = new OwnerAccountStep(
            user.FirstName,
            user.LastName,
            user.Email,
            user.PhoneNumber,
            user.IsPhoneVerified,
            user.IsEmailVerified);

        if (profile is null)
        {
            return new OwnerProfileDetails(
                null, user.Id, NotStartedStatus, false, account, null, null, null, null, null);
        }

        var identity = profile.IsIdentityComplete
            ? new OwnerIdentityStep(
                profile.IdCountryCode!,
                profile.NationalId!,
                profile.Nationality!,
                profile.BirthDate!.Value,
                profile.IdExpiryDate!.Value)
            : null;

        var address = profile.BuildingNumber is null
            ? null
            : new OwnerAddressStep(
                profile.BuildingNumber,
                profile.StreetName!,
                profile.District!,
                profile.City!,
                profile.PostalCode!,
                profile.AdditionalNumber!,
                profile.AddressProofKey is not null,
                profile.AddressProofFileName);

        return new OwnerProfileDetails(
            profile.Id,
            user.Id,
            profile.Status.ToString(),
            profile.Status == OwnerVerificationStatus.Approved,
            account,
            identity,
            address,
            profile.SubmittedAt,
            profile.ReviewedAt,
            profile.RejectionReason);
    }
}

public sealed record OwnerAccountStep(
    string FirstName,
    string LastName,
    string Email,
    string? PhoneNumber,
    bool IsPhoneVerified,
    bool IsEmailVerified);

public sealed record OwnerIdentityStep(
    string IdCountryCode,
    string NationalId,
    string Nationality,
    DateOnly BirthDate,
    DateOnly IdExpiryDate);

public sealed record OwnerAddressStep(
    string BuildingNumber,
    string StreetName,
    string District,
    string City,
    string PostalCode,
    string AdditionalNumber,
    bool HasProofImage,
    string? ProofFileName);

/// <summary>One row of the reviewers' queue. Carries no ID number or address: those are in the detail.</summary>
public sealed record OwnerReviewItem(
    Guid ProfileId,
    Guid UserId,
    string FirstName,
    string LastName,
    string Email,
    string? PhoneNumber,
    OwnerVerificationStatus Status,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? ReviewedAt,
    Guid? ReviewedByUserId);

public sealed record OwnerReviewPage(IReadOnlyList<OwnerReviewItem> Items, int TotalCount);

/// <summary>A stored address-proof image opened for reading.</summary>
public sealed record AddressProofFile(Stream Content, string ContentType, string? FileName);
