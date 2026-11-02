#nullable enable
using Microsoft.Extensions.Options;
using SmartProperty.Application.Abstractions.Storage;

namespace SmartProperty.Api.Infrastructure.Storage;

/// <summary>Where uploaded images are kept: a directory on the server's disk.</summary>
/// <remarks>
/// Configured from the "ImageStorage" section. A relative <see cref="RootPath"/> is resolved against the application's
/// content root; in a container point it at a mounted volume (<c>ImageStorage__RootPath=/data/images</c>), otherwise the
/// photos disappear with the container.
/// </remarks>
internal sealed class ImageStorageOptions
{
    public const string SectionName = "ImageStorage";

    public string RootPath { get; init; } = "App_Data/images";

    public bool IsValid() => !string.IsNullOrWhiteSpace(RootPath);
}

/// <summary>
/// Stores each image as a file under a root directory, named by its platform-generated key.
/// </summary>
/// <remarks>
/// Keys are produced by the application, never taken from a client, and are still checked here: a key may only hold
/// letters, digits, and <c>/ . _ -</c>, may not climb out of the root, and may not be rooted. A file is written to a
/// temporary name and then moved into place, so a reader never sees a half-written image.
/// </remarks>
internal sealed class LocalDiskImageStorage : IImageStorage
{
    private readonly string _root;

    public LocalDiskImageStorage(IOptions<ImageStorageOptions> options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        var configured = options.Value.RootPath;
        var combined = Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);

        _root = Path.GetFullPath(combined).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>The absolute root directory, for tests and diagnostics.</summary>
    public string RootPath => _root;

    public async Task SaveAsync(string key, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temporary = $"{path}.{Guid.NewGuid():n}.tmp";

        try
        {
            await using (var file = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await content.CopyToAsync(file, cancellationToken);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = Resolve(key);

        if (!File.Exists(path))
        {
            return Task.FromResult<Stream?>(null);
        }

        try
        {
            Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

            return Task.FromResult<Stream?>(stream);
        }
        catch (FileNotFoundException)
        {
            // Deleted between the check and the open: the same answer as never having been there.
            return Task.FromResult<Stream?>(null);
        }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        File.Delete(Resolve(key));

        return Task.CompletedTask;
    }

    private string Resolve(string key)
    {
        if (string.IsNullOrWhiteSpace(key)
            || Path.IsPathRooted(key)
            || key.Contains("..", StringComparison.Ordinal)
            || key.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '/' or '.' or '_' or '-')))
        {
            throw new ArgumentException("The storage key is not valid.", nameof(key));
        }

        var full = Path.GetFullPath(Path.Combine(_root, key));

        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("The storage key points outside the storage root.", nameof(key));
        }

        return full;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: the original failure is the one worth reporting.
        }
    }
}
