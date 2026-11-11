using System.Reflection;
using SmartProperty.Domain.Notifications;
using Xunit;

namespace SmartProperty.UnitTests.Domain.Notifications;

/// <summary>
/// Pure domain behaviour only. Persistence, uniqueness and concurrency are not covered here.
/// </summary>
public sealed class NotificationTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid Id = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid RecipientUserId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid MembershipId = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid WorkspaceId = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly Guid ResetGrantId = Guid.Parse("55555555-5555-4555-8555-555555555555");

    [Fact]
    public void CreateMemberAdded_SetsTypeVisibilitySubjectAndOccurrence()
    {
        var notification = Notification.CreateMemberAdded(Id, RecipientUserId, MembershipId, WorkspaceId, At);

        Assert.Equal(Id, notification.Id);
        Assert.Equal(RecipientUserId, notification.RecipientUserId);
        Assert.Equal("workspace.member_added", notification.Type);
        Assert.Equal(MembershipId, notification.OccurrenceId);
        Assert.Equal(NotificationVisibilityKind.Personal, notification.VisibilityKind);
        Assert.Equal("workspace", notification.SubjectType);
        Assert.Equal(WorkspaceId, notification.SubjectId);
        Assert.Empty(notification.Parameters);
        Assert.Equal(At, notification.CreatedAt);
        Assert.Null(notification.ReadAt);
    }

    [Fact]
    public void CreatePasswordChanged_SetsTypeVisibilityAndNoSubject()
    {
        var notification = Notification.CreatePasswordChanged(Id, RecipientUserId, ResetGrantId, At);

        Assert.Equal(Id, notification.Id);
        Assert.Equal(RecipientUserId, notification.RecipientUserId);
        Assert.Equal("security.password_changed", notification.Type);
        Assert.Equal(ResetGrantId, notification.OccurrenceId);
        Assert.Equal(NotificationVisibilityKind.Personal, notification.VisibilityKind);
        Assert.Null(notification.SubjectType);
        Assert.Null(notification.SubjectId);
        Assert.Empty(notification.Parameters);
        Assert.Equal(At, notification.CreatedAt);
        Assert.Null(notification.ReadAt);
    }

    [Fact]
    public void Factories_AlwaysProducePersonalVisibility()
    {
        Assert.All(CreateBoth(), notification =>
            Assert.Equal(NotificationVisibilityKind.Personal, notification.VisibilityKind));
    }

    [Fact]
    public void Factories_ProduceEmptyParameters()
    {
        Assert.All(CreateBoth(), notification => Assert.Empty(notification.Parameters));
    }

    [Fact]
    public void Factories_StartUnread()
    {
        Assert.All(CreateBoth(), notification => Assert.Null(notification.ReadAt));
    }

    [Fact]
    public void CreateMemberAdded_RejectsAnEmptyId()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            Notification.CreateMemberAdded(Guid.Empty, RecipientUserId, MembershipId, WorkspaceId, At));

        Assert.Equal("id", exception.ParamName);
    }

    [Fact]
    public void CreateMemberAdded_RejectsAnEmptyRecipient()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            Notification.CreateMemberAdded(Id, Guid.Empty, MembershipId, WorkspaceId, At));

        Assert.Equal("recipientUserId", exception.ParamName);
    }

    [Fact]
    public void CreateMemberAdded_RejectsAnEmptyMembershipId()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            Notification.CreateMemberAdded(Id, RecipientUserId, Guid.Empty, WorkspaceId, At));

        Assert.Equal("membershipId", exception.ParamName);
    }

    [Fact]
    public void CreateMemberAdded_RejectsAnEmptyWorkspaceId()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            Notification.CreateMemberAdded(Id, RecipientUserId, MembershipId, Guid.Empty, At));

        Assert.Equal("workspaceId", exception.ParamName);
    }

    [Fact]
    public void CreatePasswordChanged_RejectsAnEmptyId()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            Notification.CreatePasswordChanged(Guid.Empty, RecipientUserId, ResetGrantId, At));

        Assert.Equal("id", exception.ParamName);
    }

    [Fact]
    public void CreatePasswordChanged_RejectsAnEmptyRecipient()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            Notification.CreatePasswordChanged(Id, Guid.Empty, ResetGrantId, At));

        Assert.Equal("recipientUserId", exception.ParamName);
    }

    [Fact]
    public void CreatePasswordChanged_RejectsAnEmptyResetGrantId()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            Notification.CreatePasswordChanged(Id, RecipientUserId, Guid.Empty, At));

        Assert.Equal("resetGrantId", exception.ParamName);
    }

    [Fact]
    public void Factories_PreserveSuppliedOccurrenceIdVerbatim()
    {
        var memberAdded = Notification.CreateMemberAdded(Id, RecipientUserId, MembershipId, WorkspaceId, At);
        var passwordChanged = Notification.CreatePasswordChanged(Id, RecipientUserId, ResetGrantId, At);

        Assert.Equal(MembershipId, memberAdded.OccurrenceId);
        Assert.Equal(ResetGrantId, passwordChanged.OccurrenceId);
    }

    [Fact]
    public void RepeatedCreation_WithSameOccurrence_KeepsSameOccurrenceAndSuppliedIds()
    {
        var secondId = Guid.Parse("66666666-6666-4666-8666-666666666666");

        var first = Notification.CreateMemberAdded(Id, RecipientUserId, MembershipId, WorkspaceId, At);
        var second = Notification.CreateMemberAdded(secondId, RecipientUserId, MembershipId, WorkspaceId, At);

        Assert.Equal(first.OccurrenceId, second.OccurrenceId);
        Assert.Equal(Id, first.Id);
        Assert.Equal(secondId, second.Id);
    }

    [Fact]
    public void Factories_NormalizeCreatedAt()
    {
        var supplied = new DateTimeOffset(2026, 11, 10, 15, 0, 0, TimeSpan.FromHours(3)).AddTicks(1234567);
        var expected = At.AddTicks(1234560);

        Assert.All(CreateBoth(supplied), notification =>
        {
            Assert.Equal(expected, notification.CreatedAt);
            Assert.Equal(TimeSpan.Zero, notification.CreatedAt.Offset);
        });
    }

    [Theory]
    [InlineData(180)]
    [InlineData(-330)]
    [InlineData(840)]
    public void ToStoragePrecision_ConvertsNonZeroOffsetToUtcSameInstant(int offsetMinutes)
    {
        var offset = TimeSpan.FromMinutes(offsetMinutes);
        var local = new DateTimeOffset(At.UtcDateTime.Add(offset).Ticks, offset);

        var normalized = Notification.ToStoragePrecision(local);

        Assert.Equal(TimeSpan.Zero, normalized.Offset);
        Assert.Equal(At, normalized);
        Assert.Equal(At.UtcTicks, normalized.UtcTicks);
    }

    [Fact]
    public void ToStoragePrecision_TruncatesSubMicrosecondTicks()
    {
        var normalized = Notification.ToStoragePrecision(At.AddTicks(1234567));

        Assert.Equal(At.AddTicks(1234560), normalized);
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(1L, 0L)]
    [InlineData(5L, 0L)]
    [InlineData(9L, 0L)]
    [InlineData(10L, 10L)]
    [InlineData(11L, 10L)]
    [InlineData(19L, 10L)]
    public void ToStoragePrecision_MicrosecondBoundaries_TruncateWithoutRounding(long addedTicks, long expectedTicks)
    {
        var normalized = Notification.ToStoragePrecision(At.AddTicks(addedTicks));

        Assert.Equal(At.AddTicks(expectedTicks), normalized);
    }

    [Fact]
    public void ToStoragePrecision_IsIdempotent()
    {
        var once = Notification.ToStoragePrecision(new DateTimeOffset(2026, 11, 10, 9, 30, 0, TimeSpan.FromHours(-2)).AddTicks(7));

        Assert.Equal(once, Notification.ToStoragePrecision(once));
        Assert.Equal(TimeSpan.Zero, Notification.ToStoragePrecision(once).Offset);
    }

    [Fact]
    public void ToStoragePrecision_HandlesTheRepresentableExtremes()
    {
        var maximum = DateTimeOffset.MaxValue;

        Assert.Equal(DateTimeOffset.MinValue, Notification.ToStoragePrecision(DateTimeOffset.MinValue));
        Assert.Equal(
            maximum.Ticks - (maximum.Ticks % TimeSpan.TicksPerMicrosecond),
            Notification.ToStoragePrecision(maximum).Ticks);
    }

    [Fact]
    public void Parameters_CannotBeMutatedThroughADictionaryCast()
    {
        var notification = Notification.CreateMemberAdded(Id, RecipientUserId, MembershipId, WorkspaceId, At);
        var mutable = Assert.IsAssignableFrom<IDictionary<string, string>>(notification.Parameters);

        Assert.Throws<NotSupportedException>(() => mutable.Add("workspaceName", "Leaked"));
        Assert.Empty(notification.Parameters);
    }

    [Fact]
    public void PublicSurface_ExposesExactlyTheApprovedProperties()
    {
        var names = typeof(Notification)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] expected =
        [
            "CreatedAt",
            "Id",
            "OccurrenceId",
            "Parameters",
            "ReadAt",
            "RecipientUserId",
            "SubjectId",
            "SubjectType",
            "Type",
            "VisibilityKind"
        ];

        Assert.Equal(expected, names);
    }

    [Fact]
    public void PublicSurface_HasNoPublicSettersAndNoPublicConstructors()
    {
        var properties = typeof(Notification).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.All(properties, property => Assert.False(property.SetMethod?.IsPublic ?? false, property.Name));
        Assert.Empty(typeof(Notification).GetConstructors());
        Assert.True(typeof(Notification).IsSealed);
    }

    private static Notification[] CreateBoth()
    {
        return CreateBoth(At);
    }

    private static Notification[] CreateBoth(DateTimeOffset createdAt)
    {
        return
        [
            Notification.CreateMemberAdded(Id, RecipientUserId, MembershipId, WorkspaceId, createdAt),
            Notification.CreatePasswordChanged(Id, RecipientUserId, ResetGrantId, createdAt)
        ];
    }
}
