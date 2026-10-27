using System.Reflection;
using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.Authority;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Create;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Discard;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Submit;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Update;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.UnitTests.Application.Marketplace.ListingSubmissions.Write;

/// <summary>
/// What the write contracts can and cannot carry, and proof that nothing here is wired up: no handler, no provider
/// implementation and no way for a request to supply a trusted fact.
/// </summary>
public sealed class WriteContractPrivacyTests
{
    private static readonly Type[] Commands =
    [
        typeof(CreateListingDraftCommand),
        typeof(UpdateListingDraftCommand),
        typeof(DiscardListingDraftCommand),
        typeof(SubmitListingDraftCommand),
    ];

    // Words that would mean a request is supplying who acts, what is verified, or what was found. Matched
    // case-insensitively against every parameter name of every command and of the content input.
    private static readonly string[] ForbiddenRequestFragments =
    [
        "user",
        "acting",
        "submitter",
        "verif",
        "clean",
        "scan",
        "assessment",
        "reviewer",
        "independ",
        "legal",
        "owner",
        "party",
        "grant",
        "authority",
        "authoriz",
        "workspace",
        "sha",
        "digest",
        "hash",
        "status",
        "location",
        "country",
        "city",
        "region",
        "district",
        "propertytype",
    ];

    // ----- requests cannot carry trusted facts

    [Fact]
    public void EachCommandHasExactlyItsApprovedParameters()
    {
        Assert.Equal(new[] { "PropertyId", "Purpose", "Content" }, ParametersOf(typeof(CreateListingDraftCommand)));
        Assert.Equal(new[] { "SubmissionId", "ExpectedRevision", "Content" }, ParametersOf(typeof(UpdateListingDraftCommand)));
        Assert.Equal(new[] { "SubmissionId", "ExpectedRevision" }, ParametersOf(typeof(DiscardListingDraftCommand)));
        Assert.Equal(new[] { "SubmissionId", "ExpectedRevision" }, ParametersOf(typeof(SubmitListingDraftCommand)));
        Assert.Equal(
            new[] { "Title", "Description", "Amount", "Currency", "RentPeriod", "EvidenceIds" },
            ParametersOf(typeof(ListingDraftContentInput)));
    }

    [Fact]
    public void NoRequestParameterCanSupplyAUserAVerificationAScanResultOrReviewerOrAFactTheServerDerives()
    {
        var names = Commands.Append(typeof(ListingDraftContentInput)).SelectMany(ParametersOf).Distinct().ToArray();

        foreach (var name in names)
        {
            foreach (var fragment in ForbiddenRequestFragments)
            {
                Assert.False(
                    name.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                    $"The request parameter '{name}' contains '{fragment}'.");
            }
        }
    }

    [Fact]
    public void NoRequestParameterIsABooleanSoNoVerdictCanBeSupplied()
    {
        var types = Commands.Append(typeof(ListingDraftContentInput))
            .SelectMany(type => type.GetConstructors().Single().GetParameters().Select(parameter => parameter.ParameterType));

        Assert.DoesNotContain(types, type => (Nullable.GetUnderlyingType(type) ?? type) == typeof(bool));
    }

    [Fact]
    public void EveryCommandIsACommandOfItsResultAndNothingMore()
    {
        Assert.True(typeof(ICommand<ListingDraftResult>).IsAssignableFrom(typeof(CreateListingDraftCommand)));
        Assert.True(typeof(ICommand<ListingDraftResult>).IsAssignableFrom(typeof(UpdateListingDraftCommand)));
        Assert.True(typeof(ICommand<DiscardListingDraftResult>).IsAssignableFrom(typeof(DiscardListingDraftCommand)));
        Assert.True(typeof(ICommand<SubmitListingDraftResult>).IsAssignableFrom(typeof(SubmitListingDraftCommand)));
    }

    [Fact]
    public void TheResultsCarryNoVerificationOrIdentityDetail()
    {
        Assert.Equal(new[] { "SubmissionId", "ListingId", "Version", "Revision" }, ParametersOf(typeof(ListingDraftResult)));
        Assert.Equal(new[] { "ListingRemoved" }, ParametersOf(typeof(DiscardListingDraftResult)));
        Assert.Equal(new[] { "SubmissionId", "Status", "SubmittedAt" }, ParametersOf(typeof(SubmitListingDraftResult)));
    }

    // ----- nothing is wired up

    [Fact]
    public void NoHandlerForAnyWriteCommandExists()
    {
        var applicationTypes = typeof(ICommand<>).Assembly.GetTypes();

        foreach (var command in Commands)
        {
            var resultType = command.GetInterfaces().Single(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>)).GetGenericArguments()[0];
            var handlerType = typeof(ICommandHandler<,>).MakeGenericType(command, resultType);

            Assert.DoesNotContain(applicationTypes, type => !type.IsInterface && handlerType.IsAssignableFrom(type));
        }
    }

    [Fact]
    public void NoImplementationOfAWriteSeamExistsInTheApplication()
    {
        var applicationTypes = typeof(ICommand<>).Assembly.GetTypes().Where(type => !type.IsInterface && !type.IsAbstract).ToArray();
        var seams = new[]
        {
            typeof(IListingSubmissionWriteRepository),
            typeof(IEvidenceAssessmentProvider),
            typeof(IListingSubmissionAuditRecorder),
        };

        foreach (var seam in seams)
        {
            Assert.DoesNotContain(applicationTypes, type => seam.IsAssignableFrom(type));
        }
    }

    // ----- the repository seam

    [Fact]
    public void TheWriteRepositoryHasExactlyTheApprovedOperations()
    {
        var names = typeof(IListingSubmissionWriteRepository).GetMethods().Select(method => method.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(
            new[]
            {
                "AddListingAsync", "AddSubmissionAsync", "CanRemoveListingAfterDraftAsync", "GetGrantForSharedLockAsync",
                "GetLatestSubmissionAsync", "GetListingAsync", "GetOwnSubmissionAsync", "LockPropertyAsync",
                "RemoveDraftAsync", "RemoveListingAsync", "SaveChangesAsync",
            },
            names);
    }

    [Fact]
    public void NoWriteSeamMethodTakesAVerdictFromItsCaller()
    {
        var seams = new[] { typeof(IListingSubmissionWriteRepository), typeof(IEvidenceAssessmentProvider), typeof(IListingSubmissionAuditRecorder) };

        foreach (var method in seams.SelectMany(seam => seam.GetMethods()))
        {
            Assert.DoesNotContain(
                method.GetParameters().Select(parameter => Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType),
                type => type == typeof(bool));
        }
    }

    [Fact]
    public void GrantAndPropertyReadsAreTheOnlyWaysToObtainTheirViews()
    {
        var grantReaders = typeof(IListingSubmissionWriteRepository).GetMethods()
            .Where(method => method.ReturnType.GetGenericArguments().Any(argument => argument == typeof(AuthorityGrantView)))
            .Select(method => method.Name)
            .ToArray();
        var propertyReaders = typeof(IListingSubmissionWriteRepository).GetMethods()
            .Where(method => method.ReturnType.GetGenericArguments().Any(argument => argument == typeof(LockedPropertyView)))
            .Select(method => method.Name)
            .ToArray();

        Assert.Equal(new[] { "GetGrantForSharedLockAsync" }, grantReaders);
        Assert.Equal(new[] { "LockPropertyAsync" }, propertyReaders);
    }

    [Fact]
    public void ARegistryPropertyMayLackRegionCityOrDistrictAndTheViewSaysSo()
    {
        var view = new LockedPropertyView(Guid.NewGuid(), true, false, PropertyType.Unit, "SA", null, null, null);

        Assert.Null(view.Region);
        Assert.Null(view.City);
        Assert.Null(view.District);
    }

    // ----- views and events are closed vocabularies

    [Fact]
    public void TheGrantViewHoldsOnlyIdentifiersFlagsTimesAndEnumerationsNeverText()
    {
        foreach (var property in typeof(AuthorityGrantView).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

            Assert.True(
                type == typeof(Guid) || type == typeof(long) || type == typeof(bool) || type == typeof(DateTimeOffset)
                || type == typeof(ListingOwnerKind) || type == typeof(ListingRepresentationKind) || type == typeof(AuthorityGrantState),
                $"{property.Name} is a {type}.");
        }
    }

    [Fact]
    public void TheAuditEventHasExactlyTheApprovedTypedMembersAndNoText()
    {
        var members = typeof(ListingDraftDiscardedAuditEvent)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(property => property.Name != "EqualityContract")
            .ToArray();

        Assert.Equal(
            new[] { "ActorUserId", "EventId", "ListingDeleted", "ListingId", "OccurredAt", "Purpose", "PropertyId", "SubmissionId", "Version" }
                .Order(StringComparer.Ordinal),
            members.Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(members, property => property.PropertyType == typeof(string) || property.PropertyType == typeof(object));
    }

    [Fact]
    public void TheAuditRecorderAcceptsOnlyTheTypedEventAndDoesNotSave()
    {
        var methods = typeof(IListingSubmissionAuditRecorder).GetMethods();

        var method = Assert.Single(methods);
        Assert.Equal("RecordAsync", method.Name);
        Assert.Equal(typeof(ListingDraftDiscardedAuditEvent), method.GetParameters().First().ParameterType);
        Assert.DoesNotContain(methods, candidate => candidate.Name.Contains("Save", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnAuditEventRefusesIncompleteFacts()
    {
        var time = new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
        var id = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => new ListingDraftDiscardedAuditEvent(Guid.Empty, time, id, id, id, id, ListingPurpose.Sale, 1, false));
        Assert.Throws<ArgumentException>(() => new ListingDraftDiscardedAuditEvent(id, time, Guid.Empty, id, id, id, ListingPurpose.Sale, 1, false));
        Assert.Throws<ArgumentException>(() => new ListingDraftDiscardedAuditEvent(id, time, id, Guid.Empty, id, id, ListingPurpose.Sale, 1, false));
        Assert.Throws<ArgumentException>(() => new ListingDraftDiscardedAuditEvent(id, time, id, id, Guid.Empty, id, ListingPurpose.Sale, 1, false));
        Assert.Throws<ArgumentException>(() => new ListingDraftDiscardedAuditEvent(id, time, id, id, id, Guid.Empty, ListingPurpose.Sale, 1, false));
        Assert.Throws<ArgumentException>(() => new ListingDraftDiscardedAuditEvent(id, default, id, id, id, id, ListingPurpose.Sale, 1, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ListingDraftDiscardedAuditEvent(id, time, id, id, id, id, (ListingPurpose)99, 1, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ListingDraftDiscardedAuditEvent(id, time, id, id, id, id, ListingPurpose.Sale, 0, false));
    }

    [Fact]
    public void AValidAuditEventKeepsExactlyWhatItWasGiven()
    {
        var time = new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();

        var audit = new ListingDraftDiscardedAuditEvent(ids[0], time, ids[1], ids[2], ids[3], ids[4], ListingPurpose.Rent, 3, true);

        Assert.Equal(ids[0], audit.EventId);
        Assert.Equal(time, audit.OccurredAt);
        Assert.Equal(ids[1], audit.ActorUserId);
        Assert.Equal(ids[2], audit.SubmissionId);
        Assert.Equal(ids[3], audit.ListingId);
        Assert.Equal(ids[4], audit.PropertyId);
        Assert.Equal(ListingPurpose.Rent, audit.Purpose);
        Assert.Equal(3, audit.Version);
        Assert.True(audit.ListingDeleted);
    }

    // ----- unavailable is its own answer

    [Fact]
    public void AnUnavailableEvidenceProviderIsNeverAnEmptySuccess()
    {
        var unavailable = EvidenceResolution.Unavailable();
        var resolved = EvidenceResolution.Resolved([]);

        Assert.False(unavailable.IsAvailable);
        Assert.Null(unavailable.References);
        Assert.True(resolved.IsAvailable);
        Assert.Empty(resolved.References!);
    }

    [Fact]
    public void AnUnavailableAssessmentIsNeverAnEmptySuccess()
    {
        var unavailable = EvidenceAssessmentResult.Unavailable();
        var assessed = EvidenceAssessmentResult.Assessed([]);

        Assert.False(unavailable.IsAvailable);
        Assert.Null(unavailable.Assessments);
        Assert.True(assessed.IsAvailable);
        Assert.Empty(assessed.Assessments!);
    }

    [Fact]
    public void EvidenceResultsRefuseNullEntries()
    {
        Assert.Throws<ArgumentNullException>(() => EvidenceResolution.Resolved(null!));
        Assert.Throws<ArgumentException>(() => EvidenceResolution.Resolved([null!]));
        Assert.Throws<ArgumentNullException>(() => EvidenceAssessmentResult.Assessed(null!));
        Assert.Throws<ArgumentException>(() => EvidenceAssessmentResult.Assessed([null!]));
    }

    [Fact]
    public void ALostRaceIsATypedConflictWithAFixedMessage()
    {
        foreach (var kind in Enum.GetValues<ListingWriteConflictKind>())
        {
            var exception = new ListingWriteConflictException(kind);

            Assert.Equal(kind, exception.Kind);
            Assert.Equal("A concurrent listing write took precedence.", exception.Message);
        }

        Assert.Equal(new[] { "ListingSlot", "SubmissionVersion", "StaleRevision" }, Enum.GetNames<ListingWriteConflictKind>());
    }

    [Fact]
    public void TheConflictKindsKeepTheirNumericValuesAndTheStaleRevisionIsTheThird()
    {
        Assert.Equal(1, (int)ListingWriteConflictKind.ListingSlot);
        Assert.Equal(2, (int)ListingWriteConflictKind.SubmissionVersion);
        Assert.Equal(3, (int)ListingWriteConflictKind.StaleRevision);
        Assert.Equal(new[] { 1, 2, 3 }, Enum.GetValues<ListingWriteConflictKind>().Select(kind => (int)kind).ToArray());
    }

    [Fact]
    public void AStaleRevisionIsATypedConflictThatCarriesItsKindAndNothingElse()
    {
        var exception = new ListingWriteConflictException(ListingWriteConflictKind.StaleRevision);

        Assert.Equal(ListingWriteConflictKind.StaleRevision, exception.Kind);
        Assert.Equal("A concurrent listing write took precedence.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.Empty(exception.Data);
    }

    private static string[] ParametersOf(Type type)
    {
        return type.GetConstructors().Single().GetParameters().Select(parameter => parameter.Name!).ToArray();
    }
}
