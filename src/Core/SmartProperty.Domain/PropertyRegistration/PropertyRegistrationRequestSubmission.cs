namespace SmartProperty.Domain.PropertyRegistration;

/// <summary>
/// One immutable submitted version of a registration request: who submitted, when, and exactly what was
/// submitted. Instances can only be created by the owning request and expose no way to change.
/// </summary>
public sealed class PropertyRegistrationRequestSubmission
{
    private PropertyRegistrationRequestSubmission()
    {
        Details = null!;
    }

    internal PropertyRegistrationRequestSubmission(
        Guid id,
        int versionNumber,
        Guid submittedByUserId,
        DateTimeOffset submittedAt,
        PropertyRegistrationRequestDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);

        if (versionNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(versionNumber), versionNumber, "Version must be positive.");
        }

        Id = PropertyRegistrationGuard.RequireId(id, nameof(id));
        VersionNumber = versionNumber;
        SubmittedByUserId = PropertyRegistrationGuard.RequireId(submittedByUserId, nameof(submittedByUserId));
        SubmittedAt = submittedAt;
        Details = details;
    }

    public Guid Id { get; private set; }
    public int VersionNumber { get; private set; }
    public Guid SubmittedByUserId { get; private set; }
    public DateTimeOffset SubmittedAt { get; private set; }
    public PropertyRegistrationRequestDetails Details { get; private set; }
}
