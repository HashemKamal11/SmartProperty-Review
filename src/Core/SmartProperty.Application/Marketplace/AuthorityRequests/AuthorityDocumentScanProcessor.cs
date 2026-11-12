using System.Security.Cryptography;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Security;
using SmartProperty.Application.Abstractions.Storage;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.AuthorityRequests;

/// <summary>What one pass of the scan queue did.</summary>
public sealed record DocumentScanPassResult(int Clean, int Infected, int Retried, int Failed, int Skipped)
{
    public int Processed => Clean + Infected + Retried + Failed;
}

/// <summary>
/// Scans document versions waiting in quarantine. Run periodically by a background worker; safe to run on several
/// instances at once, because each version is locked (and skipped by others) while it is scanned.
/// </summary>
/// <remarks>
/// <para>For each due version:</para>
/// <list type="number">
/// <item>Read the quarantined bytes and check their SHA-256 against the digest recorded at upload. Missing or changed
/// bytes are a scan error, never a verdict.</item>
/// <item>Ask the scanner. <c>Clean</c>: copy the bytes to the clean area, delete the quarantined copy, mark Clean.
/// <c>Infected</c>: delete the bytes, mark Rejected with the signature. <c>Error</c> (or an exception): retry later with
/// exponential backoff, and after <see cref="DocumentScanPolicy.MaxAttempts"/> attempts mark ScanFailed.</item>
/// </list>
/// <para>When no scanner is configured nothing is processed at all: versions stay PendingScan and quarantined.</para>
/// </remarks>
public sealed class AuthorityDocumentScanProcessor(
    IAuthorityRequestRepository repository,
    IMalwareScanner scanner,
    IPrivateDocumentStorage storage,
    IDateTimeProvider clock,
    DocumentScanPolicy policy)
{
    public const string QuarantinePrefix = "quarantine/";
    public const string CleanPrefix = "documents/";

    public async Task<DocumentScanPassResult> ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        if (!scanner.IsConfigured)
        {
            return new DocumentScanPassResult(0, 0, 0, 0, 0);
        }

        var due = await repository.ListDueScansAsync(clock.UtcNow, policy.BatchSize, cancellationToken);
        int clean = 0, infected = 0, retried = 0, failed = 0, skipped = 0;

        foreach (var versionId in due)
        {
            var outcome = DocumentScanStatus.PendingScan;
            var touched = false;
            string? deleteAfterCommit = null;

            var saved = await repository.ProcessLockedScanAsync(versionId, async document =>
            {
                if (document is null || document.ScanStatus != DocumentScanStatus.PendingScan)
                {
                    return;
                }

                touched = true;
                deleteAfterCommit = await ScanOneAsync(document, cancellationToken);
                outcome = document.ScanStatus;
            }, cancellationToken);

            if (!touched || !saved)
            {
                skipped++;
                continue;
            }

            if (deleteAfterCommit is not null)
            {
                await DeleteQuietlyAsync(deleteAfterCommit);
            }

            switch (outcome)
            {
                case DocumentScanStatus.Clean: clean++; break;
                case DocumentScanStatus.Rejected: infected++; break;
                case DocumentScanStatus.ScanFailed: failed++; break;
                default: retried++; break;
            }
        }

        return new DocumentScanPassResult(clean, infected, retried, failed, skipped);
    }

    /// <summary>Scans one version; returns a key to delete once the verdict is committed, if any.</summary>
    private async Task<string?> ScanOneAsync(AuthorityRequestDocument document, CancellationToken cancellationToken)
    {
        byte[]? bytes;

        try
        {
            bytes = await ReadVerifiedAsync(document, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RecordError(document, "The stored file could not be read.");
            return null;
        }

        if (bytes is null)
        {
            RecordError(document, "The stored file is missing or does not match the digest recorded at upload.");
            return null;
        }

        MalwareScanResult result;

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            result = await scanner.ScanAsync(stream, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            result = new MalwareScanResult(MalwareScanVerdict.Error, "unknown", $"Scanner failure: {exception.GetType().Name}");
        }

        var now = clock.UtcNow;

        switch (result.Verdict)
        {
            case MalwareScanVerdict.Clean:
            {
                var cleanKey = CleanKeyFor(document.StorageKey);

                using (var copy = new MemoryStream(bytes, writable: false))
                {
                    await storage.SaveAsync(cleanKey, copy, cancellationToken);
                }

                var quarantineKey = document.StorageKey;
                document.MarkClean(result.Engine, cleanKey, now);

                return quarantineKey;
            }

            case MalwareScanVerdict.Infected:
                // The infected bytes are destroyed once the verdict is committed; the record (type, size, digest,
                // signature) stays for the history.
                document.MarkInfected(result.Engine, result.Detail ?? "malware", now);

                return document.StorageKey;

            default:
                RecordError(document, result.Detail ?? "The scanner gave no verdict.");

                return null;
        }
    }

    private void RecordError(AuthorityRequestDocument document, string error)
    {
        var now = clock.UtcNow;
        document.RecordScanError(error, policy.MaxAttempts, now + policy.DelayAfter(document.ScanAttempts + 1), now);
    }

    private async Task<byte[]?> ReadVerifiedAsync(AuthorityRequestDocument document, CancellationToken cancellationToken)
    {
        await using var stream = await storage.OpenReadAsync(document.StorageKey, cancellationToken);

        if (stream is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);

        var bytes = buffer.ToArray();
        var digest = Convert.ToHexString(SHA256.HashData(bytes));

        return bytes.LongLength == document.SizeBytes && string.Equals(digest, document.Sha256, StringComparison.Ordinal)
            ? bytes
            : null;
    }

    // The clean copy keeps the generated name and moves from quarantine/ to documents/.
    private static string CleanKeyFor(string quarantineKey) =>
        quarantineKey.StartsWith(QuarantinePrefix, StringComparison.Ordinal)
            ? CleanPrefix + quarantineKey[QuarantinePrefix.Length..]
            : quarantineKey;

    private async Task DeleteQuietlyAsync(string key)
    {
        try
        {
            await storage.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A stray quarantined copy is never served (only Clean versions are, from the clean key).
        }
    }
}
