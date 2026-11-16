namespace SmartProperty.Domain.Marketplace;

/// <summary>
/// One change of a property's owner, made by an approved ownership-transfer request. Appended, never changed: together
/// they are the property's ownership history.
/// </summary>
public sealed class PropertyOwnershipTransfer
{
    private PropertyOwnershipTransfer()
    {
    }

    public PropertyOwnershipTransfer(
        Guid id,
        Guid propertyId,
        Guid? previousOwnerUserId,
        Guid newOwnerUserId,
        Guid authorityRequestId,
        Guid? saleCommitmentId,
        Guid issuedByUserId,
        DateTimeOffset transferredAt)
    {
        if (id == Guid.Empty || propertyId == Guid.Empty || newOwnerUserId == Guid.Empty
            || authorityRequestId == Guid.Empty || issuedByUserId == Guid.Empty
            || previousOwnerUserId == Guid.Empty || saleCommitmentId == Guid.Empty)
        {
            throw new ArgumentException("The transfer's ids are required and must not be empty.");
        }

        if (previousOwnerUserId == newOwnerUserId)
        {
            throw new ArgumentException("A transfer changes the owner.", nameof(newOwnerUserId));
        }

        if (issuedByUserId == newOwnerUserId || issuedByUserId == previousOwnerUserId)
        {
            throw new ArgumentException("Neither owner issues their own transfer.", nameof(issuedByUserId));
        }

        Id = id;
        PropertyId = propertyId;
        PreviousOwnerUserId = previousOwnerUserId;
        NewOwnerUserId = newOwnerUserId;
        AuthorityRequestId = authorityRequestId;
        SaleCommitmentId = saleCommitmentId;
        IssuedByUserId = issuedByUserId;
        TransferredAt = transferredAt;
    }

    public Guid Id { get; private set; }
    public Guid PropertyId { get; private set; }

    /// <summary>Who the property was registered to before, or null when it had no recorded owner.</summary>
    public Guid? PreviousOwnerUserId { get; private set; }

    public Guid NewOwnerUserId { get; private set; }

    /// <summary>The approved ownership-transfer request.</summary>
    public Guid AuthorityRequestId { get; private set; }

    /// <summary>The completed platform sale behind it, or null for a sale outside the platform.</summary>
    public Guid? SaleCommitmentId { get; private set; }

    public Guid IssuedByUserId { get; private set; }
    public DateTimeOffset TransferredAt { get; private set; }
}

/// <summary>What an approved ownership transfer closes that the previous ownership left open.</summary>
public static class OwnershipTransferClosure
{
    /// <summary>
    /// Closes the property's open customer requests as unavailable, keeping their history: they were made to the
    /// previous owner's advertisements. Every request is validated before any changes.
    /// </summary>
    public static void CloseOpenRequests(
        IReadOnlyCollection<PurchaseRequest> purchases,
        IReadOnlyCollection<RentalRequest> rentals,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(purchases);
        ArgumentNullException.ThrowIfNull(rentals);

        foreach (var purchase in purchases)
        {
            purchase.EnsureCanCloseAsUnavailable(at);
        }

        foreach (var rental in rentals)
        {
            rental.EnsureCanCloseAsUnavailable(at);
        }

        foreach (var purchase in purchases)
        {
            purchase.CloseAsUnavailable(at);
        }

        foreach (var rental in rentals)
        {
            rental.CloseAsUnavailable(at);
        }
    }
}
