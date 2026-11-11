using System.Collections.Immutable;
using System.Reflection;
using SmartProperty.Domain.Notifications;
using Xunit;

namespace SmartProperty.UnitTests.Domain.Notifications;

public sealed class NotificationCatalogTests
{
    [Fact]
    public void ActiveTypes_ContainsExactlyTheTwoMvpTypes()
    {
        Assert.Equal(
            new[] { "workspace.member_added", "security.password_changed" },
            NotificationCatalog.ActiveTypes.ToArray());
    }

    [Fact]
    public void TypeKeyConstants_MatchTheApprovedKeys()
    {
        Assert.Equal("workspace.member_added", NotificationCatalog.MemberAddedType);
        Assert.Equal("security.password_changed", NotificationCatalog.PasswordChangedType);
        Assert.Equal("workspace", NotificationCatalog.WorkspaceSubjectType);
    }

    [Fact]
    public void Definitions_MatchActiveTypesInOrderAndAreTheSingletons()
    {
        Assert.Equal(
            NotificationCatalog.ActiveTypes.ToArray(),
            NotificationCatalog.Definitions.Select(definition => definition.Type).ToArray());
        Assert.Same(NotificationCatalog.MemberAdded, NotificationCatalog.Definitions[0]);
        Assert.Same(NotificationCatalog.PasswordChanged, NotificationCatalog.Definitions[1]);
        Assert.Same(NotificationCatalog.MemberAdded, NotificationCatalog.MemberAdded);
        Assert.Same(NotificationCatalog.PasswordChanged, NotificationCatalog.PasswordChanged);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("workspace.member_removed")]
    [InlineData("Workspace.Member_Added")]
    [InlineData("SECURITY.PASSWORD_CHANGED")]
    [InlineData(" workspace.member_added")]
    [InlineData("security.password_changed ")]
    [InlineData("owner_verification.approved")]
    public void TryGet_ReturnsFalseForUnknownNullEmptyOrDifferentlyCasedTypes(string? type)
    {
        var found = NotificationCatalog.TryGet(type, out var definition);

        Assert.False(found);
        Assert.Null(definition);
    }

    [Fact]
    public void TryGet_ReturnsTheSingletonDefinitionForEachActiveType()
    {
        Assert.True(NotificationCatalog.TryGet("workspace.member_added", out var memberAdded));
        Assert.Same(NotificationCatalog.MemberAdded, memberAdded);

        Assert.True(NotificationCatalog.TryGet("security.password_changed", out var passwordChanged));
        Assert.Same(NotificationCatalog.PasswordChanged, passwordChanged);
    }

    [Fact]
    public void VisibilityKind_DeclaresOnlyPersonal()
    {
        Assert.Equal(new[] { NotificationVisibilityKind.Personal }, Enum.GetValues<NotificationVisibilityKind>());
        Assert.Equal((short)1, (short)NotificationVisibilityKind.Personal);
        Assert.Equal(typeof(short), Enum.GetUnderlyingType(typeof(NotificationVisibilityKind)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public void VisibilityKind_DoesNotDefineReservedOrUnknownValues(int value)
    {
        Assert.False(Enum.IsDefined((NotificationVisibilityKind)(short)value));
    }

    [Fact]
    public void Definitions_AreAllPersonal()
    {
        Assert.All(NotificationCatalog.Definitions, definition =>
            Assert.Equal(NotificationVisibilityKind.Personal, definition.Visibility));
    }

    [Fact]
    public void MemberAdded_RequiresTheWorkspaceSubject()
    {
        Assert.Equal("workspace", NotificationCatalog.MemberAdded.SubjectType);
    }

    [Fact]
    public void PasswordChanged_HasNoSubject()
    {
        Assert.Null(NotificationCatalog.PasswordChanged.SubjectType);
    }

    [Fact]
    public void Definitions_AcceptNoParameterKeys()
    {
        Assert.All(NotificationCatalog.Definitions, definition => Assert.Empty(definition.AllowedParameterKeys));
    }

    [Fact]
    public void TypeDefinition_CannotBeConstructedOutsideTheDomain()
    {
        Assert.Empty(typeof(NotificationTypeDefinition).GetConstructors());
        Assert.True(typeof(NotificationTypeDefinition).IsSealed);
    }

    [Fact]
    public void TypeDefinition_HasNoSetters()
    {
        var properties = typeof(NotificationTypeDefinition).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.All(properties, property => Assert.Null(property.GetSetMethod(nonPublic: true)));
    }

    [Fact]
    public void Catalog_OffersNoRegistrationSurface()
    {
        var catalog = typeof(NotificationCatalog);

        // A static class: abstract and sealed, so it cannot be instantiated or extended.
        Assert.True(catalog.IsAbstract && catalog.IsSealed);

        var methods = catalog
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .ToArray();

        Assert.Equal(new[] { "TryGet" }, methods);
        Assert.All(
            catalog.GetProperties(BindingFlags.Public | BindingFlags.Static),
            property => Assert.Null(property.GetSetMethod(nonPublic: true)));
    }

    [Fact]
    public void CatalogCollections_AreImmutableArrays()
    {
        var catalog = typeof(NotificationCatalog);

        Assert.Equal(
            typeof(ImmutableArray<NotificationTypeDefinition>),
            catalog.GetProperty(nameof(NotificationCatalog.Definitions))!.PropertyType);
        Assert.Equal(
            typeof(ImmutableArray<string>),
            catalog.GetProperty(nameof(NotificationCatalog.ActiveTypes))!.PropertyType);
        Assert.Equal(
            typeof(ImmutableArray<string>),
            typeof(NotificationTypeDefinition).GetProperty(nameof(NotificationTypeDefinition.AllowedParameterKeys))!.PropertyType);
    }

    [Fact]
    public void CatalogCollections_RejectMutationThroughInterfaceCasts()
    {
        IList<string> activeTypes = NotificationCatalog.ActiveTypes;
        IList<NotificationTypeDefinition> definitions = NotificationCatalog.Definitions;
        IList<string> parameterKeys = NotificationCatalog.MemberAdded.AllowedParameterKeys;

        Assert.Throws<NotSupportedException>(() => activeTypes.Add("workspace.member_removed"));
        Assert.Throws<NotSupportedException>(() => activeTypes[0] = "tampered");
        Assert.Throws<NotSupportedException>(() => definitions.Clear());
        Assert.Throws<NotSupportedException>(() => parameterKeys.Add("workspaceName"));

        Assert.Equal(
            new[] { "workspace.member_added", "security.password_changed" },
            NotificationCatalog.ActiveTypes.ToArray());
        Assert.Equal(2, NotificationCatalog.Definitions.Length);
        Assert.Empty(NotificationCatalog.MemberAdded.AllowedParameterKeys);
    }
}
