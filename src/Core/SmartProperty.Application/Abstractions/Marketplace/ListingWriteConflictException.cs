namespace SmartProperty.Application.Abstractions.Marketplace;

/// <summary>
/// Which listing write collided with a concurrent one.
/// </summary>
public enum ListingWriteConflictKind
{
    /// <summary>Another writer created the listing for the same property and purpose first.</summary>
    ListingSlot = 1,

    /// <summary>Another writer created the same submission version for the listing first.</summary>
    SubmissionVersion = 2
}

/// <summary>
/// Thrown by the write repository when the database refused a listing write because a concurrent writer got there
/// first. The persistence implementation translates its own unique-constraint violations into this type, so the
/// Application never sees a provider exception and never has to know a constraint name.
/// </summary>
/// <remarks>
/// The message is fixed and carries no identifier or value. It is a conflict, not a fault: the caller reloads and
/// either returns the winner's result or reports the conflict.
/// </remarks>
public sealed class ListingWriteConflictException(ListingWriteConflictKind kind)
    : Exception("A concurrent listing write took precedence.")
{
    public ListingWriteConflictKind Kind { get; } = kind;
}
