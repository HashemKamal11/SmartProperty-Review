namespace SmartProperty.Domain.Marketplace;

/// <summary>
/// The marketplace's administrator-controlled settings. There is one row; when it does not exist the defaults apply.
/// </summary>
/// <remarks>
/// The signing window is how long, from acceptance, the parties have to sign before an unsigned commitment expires.
/// It is read when a request is accepted and stored on the commitment as a fixed deadline, so changing the setting
/// never moves a deadline that was already given.
/// </remarks>
public sealed class MarketplaceSettings
{
    public const int DefaultSigningWindowHours = 72;
    public const int MinSigningWindowHours = 1;
    public const int MaxSigningWindowHours = 720;

    private MarketplaceSettings()
    {
    }

    public MarketplaceSettings(int signingWindowHours, Guid updatedByUserId, DateTimeOffset updatedAt)
    {
        Id = 1;
        Apply(signingWindowHours, updatedByUserId, updatedAt);
    }

    public short Id { get; private set; }
    public int SigningWindowHours { get; private set; }
    public Guid UpdatedByUserId { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void ChangeSigningWindow(int signingWindowHours, Guid updatedByUserId, DateTimeOffset updatedAt)
    {
        Apply(signingWindowHours, updatedByUserId, updatedAt);
    }

    private void Apply(int signingWindowHours, Guid updatedByUserId, DateTimeOffset updatedAt)
    {
        if (signingWindowHours is < MinSigningWindowHours or > MaxSigningWindowHours)
        {
            throw new ArgumentOutOfRangeException(
                nameof(signingWindowHours), signingWindowHours,
                $"The signing window is {MinSigningWindowHours} to {MaxSigningWindowHours} hours.");
        }

        if (updatedByUserId == Guid.Empty)
        {
            throw new ArgumentException("The administrator who changed the setting is required.", nameof(updatedByUserId));
        }

        SigningWindowHours = signingWindowHours;
        UpdatedByUserId = updatedByUserId;
        UpdatedAt = updatedAt;
    }
}
