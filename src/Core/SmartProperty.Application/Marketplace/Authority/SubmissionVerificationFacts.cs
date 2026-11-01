namespace SmartProperty.Application.Marketplace.Authority;

/// <summary>
/// The nullable verification facts the Domain's <c>Submit</c> takes, as far as a verified grant can establish them.
/// </summary>
/// <remarks>
/// A null means "not established", and the Domain treats it as a failure wherever the fact is required. This type is
/// only ever produced by an evaluation that succeeded, from the grant; it is never read from a request. It does not
/// speak for the evidence or scan results, which come from the evidence provider, nor for Workspace operation
/// authority, which is not supported and therefore always stays null.
/// </remarks>
public sealed record SubmissionVerificationFacts(
    bool LegalAuthorityVerified,
    bool? ActingUserIsDeclaredOwnerVerified,
    bool? WorkspaceOperationAuthorized);
