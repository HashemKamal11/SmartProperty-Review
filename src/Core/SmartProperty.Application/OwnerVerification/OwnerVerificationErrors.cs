using SmartProperty.Common.Results;

namespace SmartProperty.Application.OwnerVerification;

internal static class OwnerVerificationErrors
{
    public static readonly Error UserNotFound = new(
        "users.not_found",
        "The user was not found.",
        ErrorType.NotFound);

    public static readonly Error ProfileNotFound = new(
        "owner_profile.not_found",
        "The owner profile was not found.",
        ErrorType.NotFound);

    public static readonly Error ProofNotFound = new(
        "owner_profile.address_proof_not_found",
        "No address proof image has been uploaded.",
        ErrorType.NotFound);

    public static readonly Error LockedForReview = new(
        "owner_profile.locked_for_review",
        "The owner profile is under review and cannot be changed.",
        ErrorType.Conflict);

    public static readonly Error NotUnderReview = new(
        "owner_profile.not_under_review",
        "Only an owner profile that is under review can be approved or rejected.",
        ErrorType.Conflict);

    public static readonly Error CannotReviewOwnProfile = new(
        "owner_profile.cannot_review_own",
        "A reviewer cannot decide their own owner profile.",
        ErrorType.Forbidden);

    public static readonly Error NationalIdTaken = new(
        "owner_profile.national_id_taken",
        "This national ID is already registered to another account.",
        ErrorType.Conflict);

    public static readonly Error ConcurrentUpdate = new(
        "owner_profile.concurrent_update",
        "The owner profile was changed by another request. Try again.",
        ErrorType.Conflict);

    public static readonly Error IdentityIncomplete = new(
        "owner_profile.identity_incomplete",
        "The national ID step must be completed before submitting.",
        ErrorType.Validation);

    public static readonly Error AddressIncomplete = new(
        "owner_profile.address_incomplete",
        "The national address step, including the address proof image, must be completed before submitting.",
        ErrorType.Validation);

    public static Error ValidationFailed(string description)
    {
        return new Error("validation.failed", description, ErrorType.Validation);
    }
}
