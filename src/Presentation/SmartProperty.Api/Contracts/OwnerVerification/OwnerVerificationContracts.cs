#nullable enable
using SmartProperty.Application.OwnerVerification;

namespace SmartProperty.Api.Contracts.OwnerVerification;

/// <summary>
/// Step 2: the national ID. <c>idCountryCode</c> is the issuing country: <c>SA</c> (national ID or Iqama, 10 digits
/// starting with 1 or 2), <c>EG</c> (14 digits; digits 2 to 7 must match <c>birthDate</c>) or <c>AE</c> (Emirates ID,
/// 15 digits starting with 784). Spaces and hyphens in <c>nationalId</c> are ignored. <c>confirmed</c> is the user's
/// statement that the data matches the original ID.
/// </summary>
public sealed record SaveOwnerIdentityRequest(
    string? IdCountryCode,
    string? NationalId,
    string? Nationality,
    DateOnly? BirthDate,
    DateOnly? IdExpiryDate,
    bool Confirmed);

/// <summary>
/// Step 3: the national address (the proof image is a separate upload). <c>buildingNumber</c> is 1 to 10 digits;
/// <c>additionalNumber</c> is optional (null or omitted), and when given 1 to 10 digits.
/// </summary>
public sealed record SaveOwnerAddressRequest(
    string? BuildingNumber,
    string? StreetName,
    string? District,
    string? City,
    string? PostalCode,
    string? AdditionalNumber,
    bool Confirmed);

public sealed record RejectOwnerProfileRequest(string? Reason);

/// <summary>
/// The owner profile with its three steps. <c>status</c> is NotStarted, Draft, UnderReview, Approved or Rejected;
/// <c>canAddProperties</c> is true only when it is Approved.
/// </summary>
public sealed record OwnerProfileResponse(
    Guid? ProfileId,
    Guid UserId,
    string Status,
    bool CanAddProperties,
    OwnerAccountStepResponse Account,
    OwnerIdentityStepResponse? Identity,
    OwnerAddressStepResponse? Address,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? ReviewedAt,
    string? RejectionReason)
{
    public static OwnerProfileResponse From(OwnerProfileDetails details)
    {
        return new OwnerProfileResponse(
            details.ProfileId,
            details.UserId,
            details.Status,
            details.CanAddProperties,
            new OwnerAccountStepResponse(
                details.Account.FirstName,
                details.Account.LastName,
                details.Account.Email,
                details.Account.PhoneNumber,
                details.Account.IsPhoneVerified,
                details.Account.IsEmailVerified),
            details.Identity is null
                ? null
                : new OwnerIdentityStepResponse(
                    details.Identity.IdCountryCode,
                    details.Identity.NationalId,
                    details.Identity.Nationality,
                    details.Identity.BirthDate,
                    details.Identity.IdExpiryDate),
            details.Address is null
                ? null
                : new OwnerAddressStepResponse(
                    details.Address.BuildingNumber,
                    details.Address.StreetName,
                    details.Address.District,
                    details.Address.City,
                    details.Address.PostalCode,
                    details.Address.AdditionalNumber,
                    details.Address.HasProofImage,
                    details.Address.ProofFileName),
            details.SubmittedAt,
            details.ReviewedAt,
            details.RejectionReason);
    }
}

public sealed record OwnerAccountStepResponse(
    string FirstName,
    string LastName,
    string Email,
    string? PhoneNumber,
    bool IsPhoneVerified,
    bool IsEmailVerified);

public sealed record OwnerIdentityStepResponse(
    string IdCountryCode,
    string NationalId,
    string Nationality,
    DateOnly BirthDate,
    DateOnly IdExpiryDate);

public sealed record OwnerAddressStepResponse(
    string BuildingNumber,
    string StreetName,
    string District,
    string City,
    string PostalCode,
    string? AdditionalNumber,
    bool HasProofImage,
    string? ProofFileName);

/// <summary>One row of the reviewers' queue.</summary>
public sealed record OwnerReviewItemResponse(
    Guid ProfileId,
    Guid UserId,
    string FirstName,
    string LastName,
    string Email,
    string? PhoneNumber,
    string Status,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? ReviewedAt,
    Guid? ReviewedByUserId);
