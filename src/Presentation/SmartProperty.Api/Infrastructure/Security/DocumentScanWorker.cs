#nullable enable
using Microsoft.Extensions.Options;
using SmartProperty.Application.Marketplace.AuthorityRequests;

namespace SmartProperty.Api.Infrastructure.Security;

/// <summary>
/// Runs the document scan queue every few seconds. Does nothing when no scanner is configured or the worker is turned
/// off on this instance. A failing pass is logged and the next pass tries again; it never marks anything clean.
/// </summary>
internal sealed class DocumentScanWorker(
    IServiceScopeFactory scopes,
    IOptions<MalwareScanningOptions> options,
    ILogger<DocumentScanWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;

        if (!settings.WorkerEnabled)
        {
            logger.LogInformation("The document scan worker is turned off on this instance.");
            return;
        }

        if (!settings.IsConfigured)
        {
            logger.LogWarning(
                "No malware scanner is configured (MalwareScanning:ClamAvHost). Uploaded documents stay quarantined in "
                + "PendingScan and authority requests cannot be submitted until one is configured.");
            return;
        }

        var interval = TimeSpan.FromSeconds(settings.PollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<AuthorityDocumentScanProcessor>();
                var result = await processor.ProcessDueAsync(stoppingToken);

                if (result.Processed > 0)
                {
                    logger.LogInformation(
                        "Document scan pass: {Clean} clean, {Infected} infected, {Retried} to retry, {Failed} failed.",
                        result.Clean, result.Infected, result.Retried, result.Failed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError("Document scan pass failed: {ExceptionType}. It is retried on the next pass.", exception.GetType().Name);
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
