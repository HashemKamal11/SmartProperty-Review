using SmartProperty.Domain.PropertyRegistration;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;
using static SmartProperty.UnitTests.Domain.PropertyRegistration.PropertyRegistrationFixtures;

namespace SmartProperty.UnitTests.Domain.PropertyRegistration;

/// <summary>
/// Covers the immutability of submitted versions and reviewer decisions: later edits must not reach history, and
/// the exposed collections must not be a way to change it.
/// </summary>
public sealed class PropertyRegistrationRequestHistoryTests
{
    [Fact]
    public void SubmittedSnapshot_IsUnaffectedByLaterEditsOfTheWorkingDetails()
    {
        var request = CreateChangesRequired();
        var snapshot = request.Submissions[0].Details;
        var bedroomsAtSubmission = snapshot.Bedrooms;
        var addressAtSubmission = snapshot.Address;

        request.UpdateDetails(
            new PropertyRegistrationRequestDetails(
                PropertyType.Unit,
                new PropertyAddress("SA", city: "Jeddah", district: "Rawdah", addressLine: "Tower 9"),
                300m,
                7,
                5,
                1990),
            Editor,
            At(3));

        Assert.Same(snapshot, request.Submissions[0].Details);
        Assert.Equal(bedroomsAtSubmission, request.Submissions[0].Details.Bedrooms);
        Assert.Equal(addressAtSubmission, request.Submissions[0].Details.Address);
        Assert.Equal("Riyadh", request.Submissions[0].Details.Address!.City);
        Assert.Equal(120m, request.Submissions[0].Details.Area);
        Assert.NotEqual(request.Details, request.Submissions[0].Details);
    }

    [Fact]
    public void EveryVersionKeepsItsOwnContentAcrossResubmissions()
    {
        var request = CreateChangesRequired();
        request.UpdateDetails(CompleteUnit(bedrooms: 6), Editor, At(3));
        request.Resubmit(Guid.NewGuid(), Editor, At(4));
        request.RequestChanges(Guid.NewGuid(), Reviewer, "Again.", At(5));
        request.UpdateDetails(CompleteUnit(bedrooms: 8), Editor, At(6));
        request.Resubmit(Guid.NewGuid(), Editor, At(7));

        Assert.Equal([1, 2, 3], request.Submissions.Select(s => s.VersionNumber));
        Assert.Equal([3, 6, 8], request.Submissions.Select(s => s.Details.Bedrooms));
        Assert.Equal([1, 2], request.Reviews.Select(r => r.SubmissionVersion));
    }

    [Fact]
    public void SubmissionCollection_CannotBeModifiedThroughCasts()
    {
        var request = CreateUnderReview();
        var submissions = request.Submissions;

        Assert.False(submissions is List<PropertyRegistrationRequestSubmission>);
        var list = Assert.IsAssignableFrom<IList<PropertyRegistrationRequestSubmission>>(submissions);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Clear());
        Assert.Throws<NotSupportedException>(() => list.RemoveAt(0));
        Assert.Throws<NotSupportedException>(() => list.Add(request.Submissions[0]));

        Assert.Single(request.Submissions);
    }

    [Fact]
    public void ReviewCollection_CannotBeModifiedThroughCasts()
    {
        var request = CreateApproved();
        var reviews = request.Reviews;

        Assert.False(reviews is List<PropertyRegistrationRequestReview>);
        var list = Assert.IsAssignableFrom<IList<PropertyRegistrationRequestReview>>(reviews);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Clear());
        Assert.Throws<NotSupportedException>(() => list.RemoveAt(0));

        Assert.Single(request.Reviews);
    }

    [Fact]
    public void RetainedCollectionReference_ReflectsOnlyOperationsTheAggregateAccepted()
    {
        var request = CreateDraft();
        var before = request.Submissions;
        Assert.Empty(before);

        request.Submit(Guid.NewGuid(), Creator, At(1));

        // The aggregate hands out a fresh read-only view each time; an older view is never a way to add history.
        Assert.IsAssignableFrom<IReadOnlyList<PropertyRegistrationRequestSubmission>>(before);
        Assert.Single(request.Submissions);
    }

    [Fact]
    public void SubmissionsAndReviews_ExposeNoPublicSetters()
    {
        AssertNoPublicSetters(typeof(PropertyRegistrationRequestSubmission));
        AssertNoPublicSetters(typeof(PropertyRegistrationRequestReview));
        AssertNoPublicSetters(typeof(PropertyRegistrationRequestWithdrawal));
        AssertNoPublicSetters(typeof(PropertyRegistrationRequestDetails));
        AssertNoPublicSetters(typeof(PropertyAddress));
    }

    [Fact]
    public void SubmissionsAndReviews_HaveNoPublicConstructors()
    {
        Assert.Empty(typeof(PropertyRegistrationRequestSubmission).GetConstructors());
        Assert.Empty(typeof(PropertyRegistrationRequestReview).GetConstructors());
        Assert.Empty(typeof(PropertyRegistrationRequestWithdrawal).GetConstructors());
    }

    [Fact]
    public void Withdrawal_IsFrozenOnceRecorded()
    {
        var request = CreateWithdrawn();
        var withdrawal = request.Withdrawal;

        Assert.Throws<InvalidOperationException>(() => request.Withdraw(Editor, "Again.", At(10)));

        Assert.Same(withdrawal, request.Withdrawal);
        Assert.Equal(Creator, request.Withdrawal!.WithdrawnByUserId);
    }

    [Fact]
    public void EnumValues_AreStable()
    {
        Assert.Equal(1, (int)PropertyRegistrationRequestStatus.Draft);
        Assert.Equal(2, (int)PropertyRegistrationRequestStatus.UnderReview);
        Assert.Equal(3, (int)PropertyRegistrationRequestStatus.ChangesRequired);
        Assert.Equal(4, (int)PropertyRegistrationRequestStatus.Approved);
        Assert.Equal(5, (int)PropertyRegistrationRequestStatus.Rejected);
        Assert.Equal(6, (int)PropertyRegistrationRequestStatus.Withdrawn);
        Assert.Equal(1, (int)PropertyRegistrationDecision.Approved);
        Assert.Equal(2, (int)PropertyRegistrationDecision.ChangesRequested);
        Assert.Equal(3, (int)PropertyRegistrationDecision.Rejected);
    }

    private static void AssertNoPublicSetters(Type type)
    {
        foreach (var property in type.GetProperties())
        {
            var setter = property.SetMethod;
            Assert.True(
                setter is null || !setter.IsPublic,
                $"{type.Name}.{property.Name} exposes a public setter.");
        }
    }
}
