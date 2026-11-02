using SmartProperty.Application.Abstractions.Storage;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>An in-memory image store that records what was saved and deleted, and can be told to fail.</summary>
internal sealed class FakeImageStorage : IImageStorage
{
    public Dictionary<string, byte[]> Files { get; } = [];

    public List<string> Saved { get; } = [];

    public List<string> Deleted { get; } = [];

    /// <summary>When set, the save with this zero-based number throws, as a full disk would.</summary>
    public int? FailOnSaveNumber { get; set; }

    /// <summary>When true, deleting throws an <see cref="IOException"/>.</summary>
    public bool FailOnDelete { get; set; }

    public async Task SaveAsync(string key, Stream content, CancellationToken cancellationToken = default)
    {
        if (FailOnSaveNumber == Saved.Count)
        {
            throw new IOException("Simulated disk failure.");
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);

        Files[key] = buffer.ToArray();
        Saved.Add(key);
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        Stream? stream = Files.TryGetValue(key, out var bytes) ? new MemoryStream(bytes) : null;

        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        if (FailOnDelete)
        {
            throw new IOException("Simulated delete failure.");
        }

        Deleted.Add(key);
        Files.Remove(key);

        return Task.CompletedTask;
    }
}
