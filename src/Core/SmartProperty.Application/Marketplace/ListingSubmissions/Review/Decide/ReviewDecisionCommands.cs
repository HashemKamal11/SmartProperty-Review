using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Review.Decide;

/// <summary>Approves a pending submission and publishes it. The reviewer is the authenticated user.</summary>
public sealed record ApproveListingSubmissionCommand(Guid SubmissionId) : ICommand<ReviewDecisionResult>;

/// <summary>Rejects a pending submission with a reason written for the submitter.</summary>
public sealed record RejectListingSubmissionCommand(Guid SubmissionId, string Reason) : ICommand<ReviewDecisionResult>;

/// <summary>The outcome of a decision. It carries no reason and no reviewer detail.</summary>
public sealed record ReviewDecisionResult(
    Guid SubmissionId,
    Guid ListingId,
    ListingSubmissionStatus Status,
    DateTimeOffset DecidedAt);
