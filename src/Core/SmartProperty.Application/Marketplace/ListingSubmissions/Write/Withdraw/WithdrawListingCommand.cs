using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write.Withdraw;

/// <summary>Withdraws the caller's own published, open listing from the marketplace.</summary>
public sealed record WithdrawListingCommand(Guid ListingId) : ICommand<WithdrawListingResult>;

public sealed record WithdrawListingResult(Guid ListingId, DateTimeOffset WithdrawnAt);
