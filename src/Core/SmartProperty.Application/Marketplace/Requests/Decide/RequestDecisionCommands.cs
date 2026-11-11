using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Marketplace.Requests.Decide;

/// <summary>A customer withdraws their own open request.</summary>
public sealed record WithdrawRequestCommand(RequestKind Kind, Guid RequestId) : ICommand<RequestDecisionResult>;

/// <summary>The advertiser rejects an open request made against a version they authored.</summary>
public sealed record RejectRequestCommand(RequestKind Kind, Guid RequestId) : ICommand<RequestDecisionResult>;

/// <summary>
/// The advertiser accepts an open request. The property is then committed, the property's current advertisements
/// close, and every other open request is closed with its history kept.
/// </summary>
public sealed record AcceptRequestCommand(RequestKind Kind, Guid RequestId) : ICommand<AcceptRequestResult>;

public sealed record RequestDecisionResult(RequestKind Kind, Guid RequestId, string Status, DateTimeOffset DecidedAt);

public sealed record AcceptRequestResult(
    RequestKind Kind,
    Guid RequestId,
    Guid CommitmentId,
    string Status,
    DateTimeOffset AcceptedAt,
    DateTimeOffset SigningDeadline);

public enum CommitmentStep
{
    /// <summary>The owner confirms the agreement was signed.</summary>
    Signed = 1,

    /// <summary>The owner confirms the handover to the customer.</summary>
    Handover = 2,

    /// <summary>The owner confirms the tenant returned the property.</summary>
    Return = 3
}

/// <summary>The owner records a step of a commitment. These are statements by the owner, not legal proof.</summary>
public sealed record ConfirmCommitmentStepCommand(Guid CommitmentId, CommitmentStep Step) : ICommand<CommitmentStepResult>;

public sealed record CommitmentStepResult(Guid CommitmentId, string Status, DateTimeOffset At);
