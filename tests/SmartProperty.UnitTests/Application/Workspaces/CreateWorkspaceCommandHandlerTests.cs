using SmartProperty.Application.Workspaces.Administration.Create;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Workspaces;

public sealed class CreateWorkspaceCommandHandlerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\tName")]
    [InlineData("Name\n")]
    [InlineData("A\0B")]
    public async Task InvalidNamesDoNotSave(string? name)
    {
        var save = new FakeUnitOfWork();
        var handler = new CreateWorkspaceCommandHandler(new FakeWorkspaceRepository(), new FakeDateTimeProvider(DateTimeOffset.UtcNow), save);
        var result = await handler.Handle(new(name));
        Assert.Equal("workspace_admin.validation_failed", result.Error!.Code);
        Assert.Equal(0, save.SaveChangesCallCount);
    }

    [Fact]
    public async Task Utf16BoundAndUnicodePreservation()
    {
        var repository = new FakeWorkspaceRepository();
        var save = new FakeUnitOfWork();
        var now = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
        var handler = new CreateWorkspaceCommandHandler(repository, new FakeDateTimeProvider(now), save);
        var name = string.Concat(Enumerable.Repeat("😀", 100));
        var valid = await handler.Handle(new("  " + name + "  "));
        Assert.True(valid.IsSuccess);
        Assert.Equal(name, valid.Value.Name);
        Assert.Equal(now, valid.Value.CreatedAt);
        Assert.Equal(now, valid.Value.UpdatedAt);
        Assert.NotEqual(Guid.Empty, valid.Value.Id);
        Assert.NotNull(await repository.GetByIdAsync(valid.Value.Id));
        Assert.True((await handler.Handle(new(name + "x"))).IsFailure);
        var unicode = await handler.Handle(new("  مصر  AbC  "));
        Assert.Equal("مصر  AbC", unicode.Value.Name);
        Assert.Equal(2, save.SaveChangesCallCount);
    }

    [Fact]
    public async Task SaveFailurePropagatesWithoutRetry()
    {
        var save = new FakeUnitOfWork { ExceptionToThrow = new InvalidOperationException("test failure") };
        var handler = new CreateWorkspaceCommandHandler(new FakeWorkspaceRepository(), new FakeDateTimeProvider(DateTimeOffset.UtcNow), save);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(new("Name")));
        Assert.Equal(1, save.SaveChangesCallCount);
    }
}