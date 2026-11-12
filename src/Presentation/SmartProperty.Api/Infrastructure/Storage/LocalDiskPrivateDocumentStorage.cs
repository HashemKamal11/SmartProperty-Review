#nullable enable
using Microsoft.Extensions.Options;
using SmartProperty.Application.Abstractions.Storage;

namespace SmartProperty.Api.Infrastructure.Storage;

/// <summary>
/// Where legal documents are kept: the "DocumentStorage" section. In a container, point <see cref="RootPath"/> at its
/// own persistent volume (<c>DocumentStorage__RootPath=/data/documents</c>), separate from the photos, and back it up.
/// </summary>
internal sealed class DocumentStorageOptions
{
    public const string SectionName = "DocumentStorage";

    public string RootPath { get; init; } = "App_Data/documents";
}

/// <summary>
/// Legal documents on the server's disk, under a root that is never served statically: bytes leave only through the
/// authorized download endpoints. Same key rules and atomic writes as the photo store.
/// </summary>
internal sealed class LocalDiskPrivateDocumentStorage : IPrivateDocumentStorage
{
    private readonly LocalDiskImageStorage _store;

    public LocalDiskPrivateDocumentStorage(IOptions<DocumentStorageOptions> options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);

        RootPath = LocalDiskImageStorage.ResolveRoot(options.Value.RootPath, environment);
        _store = new LocalDiskImageStorage(RootPath);
    }

    public string RootPath { get; }

    public Task SaveAsync(string key, Stream content, CancellationToken cancellationToken = default) =>
        _store.SaveAsync(key, content, cancellationToken);

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
        _store.OpenReadAsync(key, cancellationToken);

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        _store.DeleteAsync(key, cancellationToken);
}
