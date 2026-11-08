namespace SmartProperty.Domain.OwnerVerification;

/// <summary>Where an owner profile is in its review.</summary>
/// <remarks>The numeric values are stored; never reassign them.</remarks>
public enum OwnerVerificationStatus
{
    /// <summary>Being filled in or corrected by the user. Not visible to reviewers.</summary>
    Draft = 1,

    /// <summary>Submitted and waiting for a reviewer. Locked: the user cannot change it.</summary>
    UnderReview = 2,

    /// <summary>Approved by a reviewer. The user may add properties.</summary>
    Approved = 3,

    /// <summary>Rejected with a reason. The user corrects it and submits again.</summary>
    Rejected = 4
}
