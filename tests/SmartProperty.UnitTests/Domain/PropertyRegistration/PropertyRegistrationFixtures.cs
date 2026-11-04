using SmartProperty.Domain.PropertyRegistration;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.UnitTests.Domain.PropertyRegistration;

/// <summary>
/// Builders shared by the registration tests. Every time value is supplied by the test; nothing reads a clock.
/// </summary>
internal static class PropertyRegistrationFixtures
{
    public static readonly DateTimeOffset CreatedAt = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public static readonly Guid Creator = Guid.NewGuid();
    public static readonly Guid Editor = Guid.NewGuid();
    public static readonly Guid Reviewer = Guid.NewGuid();

    public static DateTimeOffset At(int hours) => CreatedAt.AddHours(hours);

    public static PropertyAddress CompleteAddress() =>
        new("SA", city: "Riyadh", district: "Hittin", addressLine: "Villa 12, King Fahd Rd");

    /// <summary>Complete for every property type, including a unit.</summary>
    public static PropertyRegistrationRequestDetails CompleteUnit(int bedrooms = 3) =>
        new(PropertyType.Unit, CompleteAddress(), 120m, bedrooms, 2, 2015);

    public static PropertyRegistrationRequestDetails CompleteBuilding() =>
        new(PropertyType.Building, CompleteAddress(), 800m);

    public static PropertyRegistrationRequest CreateDraft(PropertyRegistrationRequestDetails? details = null) =>
        new(Guid.NewGuid(), "PRR-0001", Guid.NewGuid(), Creator, details ?? CompleteUnit(), CreatedAt);

    public static PropertyRegistrationRequest CreateUnderReview()
    {
        var request = CreateDraft();
        request.Submit(Guid.NewGuid(), Creator, At(1));
        return request;
    }

    public static PropertyRegistrationRequest CreateChangesRequired()
    {
        var request = CreateUnderReview();
        request.RequestChanges(Guid.NewGuid(), Reviewer, "Fix the address.", At(2));
        return request;
    }

    public static PropertyRegistrationRequest CreateApproved()
    {
        var request = CreateUnderReview();
        request.Approve(Guid.NewGuid(), Reviewer, Guid.NewGuid(), "Verified.", At(2));
        return request;
    }

    public static PropertyRegistrationRequest CreateRejected()
    {
        var request = CreateUnderReview();
        request.Reject(Guid.NewGuid(), Reviewer, "Not a real asset.", At(2));
        return request;
    }

    public static PropertyRegistrationRequest CreateWithdrawn()
    {
        var request = CreateUnderReview();
        request.Withdraw(Creator, "No longer needed.", At(2));
        return request;
    }
}
