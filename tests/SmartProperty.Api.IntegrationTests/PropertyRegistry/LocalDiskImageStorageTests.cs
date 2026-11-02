using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SmartProperty.Api.Infrastructure.Storage;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.PropertyRegistry;

/// <summary>The disk storage on its own: what it keeps, and what it refuses to touch.</summary>
public sealed class LocalDiskImageStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "smartproperty-storage-" + Guid.NewGuid().ToString("N"));
    private readonly LocalDiskImageStorage _storage;

    public LocalDiskImageStorageTests()
    {
        _storage = new LocalDiskImageStorage(
            Options.Create(new ImageStorageOptions { RootPath = _root }),
            new StubEnvironment(Path.GetTempPath()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task ASavedFileCanBeReadBackAndNoTemporaryFileIsLeft()
    {
        await _storage.SaveAsync("p1/i1.jpg", new MemoryStream([1, 2, 3, 4]));

        await using var stream = await _storage.OpenReadAsync("p1/i1.jpg");
        using var copy = new MemoryStream();
        await stream!.CopyToAsync(copy);

        Assert.Equal([1, 2, 3, 4], copy.ToArray());
        Assert.Single(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SavingAgainOverwrites()
    {
        await _storage.SaveAsync("p/i.jpg", new MemoryStream([1]));
        await _storage.SaveAsync("p/i.jpg", new MemoryStream([9, 9]));

        await using var stream = await _storage.OpenReadAsync("p/i.jpg");

        Assert.Equal(2, stream!.Length);
    }

    [Fact]
    public async Task AMissingFileOpensAsNullAndDeletingItIsFine()
    {
        Assert.Null(await _storage.OpenReadAsync("nobody/home.jpg"));
        await _storage.DeleteAsync("nobody/home.jpg");
    }

    [Fact]
    public async Task DeleteRemovesTheFile()
    {
        await _storage.SaveAsync("p/i.png", new MemoryStream([1]));

        await _storage.DeleteAsync("p/i.png");

        Assert.Null(await _storage.OpenReadAsync("p/i.png"));
    }

    [Theory]
    [InlineData("../escape.jpg")]
    [InlineData("a/../../escape.jpg")]
    [InlineData("..\\escape.jpg")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("a b.jpg")]
    [InlineData("a:b.jpg")]
    [InlineData("")]
    [InlineData("  ")]
    public async Task AKeyThatCouldLeaveTheRootOrIsOddlyShapedIsRefusedEverywhere(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.SaveAsync(key, new MemoryStream([1])));
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.OpenReadAsync(key));
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.DeleteAsync(key));
    }

    [Fact]
    public async Task ARelativeRootIsResolvedAgainstTheContentRoot()
    {
        var relative = new LocalDiskImageStorage(
            Options.Create(new ImageStorageOptions { RootPath = "rel-" + Guid.NewGuid().ToString("N") }),
            new StubEnvironment(_root));

        await relative.SaveAsync("k/a.jpg", new MemoryStream([1]));

        Assert.StartsWith(_root, relative.RootPath);
        Assert.True(File.Exists(Path.Combine(relative.RootPath, "k", "a.jpg")));
    }

    private sealed class StubEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Test";

        public string ContentRootPath { get; set; } = contentRoot;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
