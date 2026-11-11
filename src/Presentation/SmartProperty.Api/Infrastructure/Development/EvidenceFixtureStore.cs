#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SmartProperty.Api.Infrastructure.Development;

/// <summary>A demo evidence fixture someone registered. It is not real evidence and is labelled so everywhere it appears.</summary>
public sealed record EvidenceFixture(
    Guid EvidenceId,
    Guid VersionId,
    Guid PropertyId,
    Guid RegisteredByUserId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    DateTimeOffset RegisteredAt)
{
    public const string Label = "SIMULATED demo fixture: no external scan or document verification took place.";
}

/// <summary>
/// Where Development-only evidence fixtures are registered and kept: the file itself, its SHA-256, who registered it for
/// which property, and when. Only fixtures registered here are recognised by the simulated assessment provider.
/// </summary>
/// <remarks>
/// A plain directory and a JSON index, so demo mode needs no schema change. An id that is not in the index is unknown,
/// and the provider treats an unknown id as a failure, never as clean. Every fixture belongs to the user who
/// registered it and to one property.
/// </remarks>
public sealed class EvidenceFixtureStore
{
    /// <summary>A fixture that contains this text is assessed as Rejected, so the failure path can be demonstrated.</summary>
    public const string RejectMarker = "SMARTPROPERTY-DEMO-REJECT-ME";

    private const string IndexFileName = "fixtures.json";

    private readonly string _root;
    private readonly long _maxBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<Guid, EvidenceFixture>? _index;

    public EvidenceFixtureStore(IOptions<DemoModeOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _root = options.Value.EvidenceRootPath;
        _maxBytes = options.Value.MaxEvidenceBytes;
    }

    public long MaxBytes => _maxBytes;

    public async Task<EvidenceFixture> RegisterAsync(
        Guid userId,
        Guid propertyId,
        string fileName,
        string contentType,
        Stream content,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (userId == Guid.Empty || propertyId == Guid.Empty)
        {
            throw new ArgumentException("A user and a property are required.");
        }

        var evidenceId = Guid.NewGuid();
        var path = FilePath(evidenceId);

        await _gate.WaitAsync(cancellationToken);

        try
        {
            Directory.CreateDirectory(_root);

            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await content.CopyToAsync(file, cancellationToken);
            }

            var info = new FileInfo(path);

            if (info.Length == 0 || info.Length > _maxBytes)
            {
                File.Delete(path);

                throw new InvalidOperationException($"A fixture must be between 1 byte and {_maxBytes} bytes.");
            }

            var fixture = new EvidenceFixture(
                evidenceId,
                Guid.NewGuid(),
                propertyId,
                userId,
                SafeFileName(fileName),
                string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Trim(),
                info.Length,
                await HashAsync(path, cancellationToken),
                at);

            var index = await LoadAsync(cancellationToken);
            index[evidenceId] = fixture;
            await SaveAsync(index, cancellationToken);

            return fixture;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EvidenceFixture?> FindAsync(Guid evidenceId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            return (await LoadAsync(cancellationToken)).GetValueOrDefault(evidenceId);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// What the stored file says now: whether it still has the registered digest, and whether it carries the reject
    /// marker. A missing or altered file is not clean.
    /// </summary>
    public async Task<(bool Intact, bool Marked)> InspectAsync(EvidenceFixture fixture, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var path = FilePath(fixture.EvidenceId);

        if (!File.Exists(path))
        {
            return (false, false);
        }

        var intact = string.Equals(await HashAsync(path, cancellationToken), fixture.Sha256, StringComparison.OrdinalIgnoreCase);
        var text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path, cancellationToken));

        return (intact, text.Contains(RejectMarker, StringComparison.Ordinal));
    }

    internal string FilePath(Guid evidenceId) => Path.Combine(_root, $"{evidenceId:N}.bin");

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);

        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static string SafeFileName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty);

        return string.IsNullOrWhiteSpace(name) ? "fixture" : name.Length > 120 ? name[..120] : name;
    }

    private async Task<Dictionary<Guid, EvidenceFixture>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_index is not null)
        {
            return _index;
        }

        var path = Path.Combine(_root, IndexFileName);
        _index = File.Exists(path)
            ? (JsonSerializer.Deserialize<List<EvidenceFixture>>(await File.ReadAllTextAsync(path, cancellationToken)) ?? [])
                .ToDictionary(fixture => fixture.EvidenceId)
            : [];

        return _index;
    }

    private async Task SaveAsync(Dictionary<Guid, EvidenceFixture> index, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, IndexFileName);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(index.Values.OrderBy(f => f.RegisteredAt)), cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }
}
