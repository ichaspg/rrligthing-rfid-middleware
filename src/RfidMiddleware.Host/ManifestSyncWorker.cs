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
        var authorization = await repository.GetAuthorizationStateAsync(stoppingToken);
        RestoreAuthorizationState(authorization);
        var restored = await repository.GetActiveAsync(stoppingToken);
        if (restored is not null) Activate(restored);
        else if (authorization.State == AuthorizationState.Active)
            throw new InvalidDataException("Authorization state is active but no active manifest could be restored.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await client.GetCurrentAsync(_etag, stoppingToken);
                if (result.Directive == ManifestFetchDirective.Activate && result.Manifest is not null)
                {
                    if (ShouldApply(result.Manifest.Version))
                    {
                        await repository.StageAndActivateAsync(result.Manifest, stoppingToken);
                        var snapshot = await repository.GetActiveAsync(stoppingToken)
                            ?? throw new InvalidOperationException("Activated manifest could not be restored.");
                        Activate(snapshot);
                    }
                }
                else if (result.Directive == ManifestFetchDirective.Deactivate && result.Completion is not null)
                {
                    if (ShouldApply(result.Completion.Version))
                    {
                        await repository.DeactivateAsync(result.Completion, stoppingToken);
                        Deactivate(result.Completion);
                    }
                }
                if (result.Directive != ManifestFetchDirective.Unchanged) _etag = result.ETag;
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
        state.AuthorizationState = AuthorizationState.Active;
        state.LatestManifestVersion = snapshot.Version;
        state.ActiveManifestId = snapshot.ManifestId;
        state.ActiveManifestVersion = snapshot.Version;
        state.ManifestExpiresAt = snapshot.ExpiresAt;
        state.LastManifestSync ??= snapshot.ActivatedAt;
        logger.LogInformation("Activated manifest {ManifestId} version {Version} with {Count} EPCs",
            snapshot.ManifestId, snapshot.Version, snapshot.Epcs.Count);
    }

    private void Deactivate(ManifestCompletion completion)
    {
        validation.Deactivate();
        state.AuthorizationState = AuthorizationState.Completed;
        state.LatestManifestVersion = completion.Version;
        state.ActiveManifestId = null;
        state.ActiveManifestVersion = null;
        state.ManifestExpiresAt = null;
        logger.LogInformation("Completed manifest {ManifestId} at version {Version}",
            completion.ManifestId, completion.Version);
    }

    private void RestoreAuthorizationState(AuthorizationStateSnapshot authorization)
    {
        state.AuthorizationState = authorization.State;
        state.LatestManifestVersion = authorization.Version;
        if (authorization.State == AuthorizationState.Completed)
        {
            validation.Deactivate();
            state.ActiveManifestId = null;
            state.ActiveManifestVersion = null;
            state.ManifestExpiresAt = null;
        }
    }

    private bool ShouldApply(long version)
    {
        if (state.LatestManifestVersion is null) return true;
        if (version < state.LatestManifestVersion)
            throw new InvalidDataException(
                $"Received manifest version {version}, but newer version {state.LatestManifestVersion} is already applied.");
        return version > state.LatestManifestVersion;
    }
}
