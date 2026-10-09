using RfidMiddleware.Core;

namespace RfidMiddleware.Host;

public sealed class ManifestSyncWorker(
    ILogger<ManifestSyncWorker> logger,
    MiddlewareOptions options,
    IManifestRepository repository,
    IManifestClient client,
    ValidationEngine validation,
    RuntimeState state,
    ManifestSyncTrigger trigger) : BackgroundService
{
    private string? _etag;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await repository.InitializeAsync(stoppingToken);
        var restored = await repository.GetActiveAsync(stoppingToken);
        if (restored is not null) Activate(restored);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await client.GetCurrentAsync(_etag, stoppingToken);
                if (result.Changed && result.Manifest is not null)
                {
                    var current = validation.Snapshot;
                    if (current is null || result.Manifest.Version > current.Version)
                    {
                        await repository.StageAndActivateAsync(result.Manifest, stoppingToken);
                        var snapshot = await repository.GetActiveAsync(stoppingToken)
                            ?? throw new InvalidOperationException("Activated manifest could not be restored.");
                        Activate(snapshot);
                    }
                    _etag = result.ETag;
                }
                state.LastManifestSync = DateTimeOffset.UtcNow;
                state.LastManifestError = null;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                state.LastManifestError = ex.Message;
                logger.LogError(ex, "Manifest synchronization failed");
            }

            await trigger.WaitAsync(TimeSpan.FromSeconds(options.ManifestPollSeconds), stoppingToken);
        }
    }

    private void Activate(ManifestSnapshot snapshot)
    {
        validation.Activate(snapshot);
        state.ActiveManifestId = snapshot.ManifestId;
        state.ActiveManifestVersion = snapshot.Version;
        state.ManifestExpiresAt = snapshot.ExpiresAt;
        state.LastManifestSync ??= snapshot.ActivatedAt;
        logger.LogInformation("Activated manifest {ManifestId} version {Version} with {Count} EPCs",
            snapshot.ManifestId, snapshot.Version, snapshot.Epcs.Count);
    }
}
