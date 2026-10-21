using RfidMiddleware.Core;

namespace RfidMiddleware.Host;

public sealed record ManifestPushRequest(
    string ManifestId,
    string? TransportId,
    long Version,
    string Status,
    int ItemCount,
    string? Checksum,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<ManifestPushItem>? Items);

public sealed record ManifestPushItem(string InventoryId, string Epc);

public sealed record ManifestPushResult(
    string MiddlewareId,
    string ManifestId,
    long Version,
    string Status,
    bool Applied,
    bool AlreadyApplied,
    DateTimeOffset ProcessedAt);

public sealed class ManifestVersionConflictException(string message, long? currentVersion = null) : Exception(message)
{
    public long? CurrentVersion { get; } = currentVersion;
}

public sealed class ManifestPushService(
    MiddlewareOptions options,
    IManifestRepository repository,
    ValidationEngine validation,
    RuntimeState state,
    IClock clock,
    ILogger<ManifestPushService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ManifestPushResult> ApplyAsync(ManifestPushRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var status = request.Status?.Trim().ToUpperInvariant();
            if (status is not ("ACTIVE" or "COMPLETED"))
                throw new InvalidDataException("Status must be ACTIVE or COMPLETED.");
            if (string.IsNullOrWhiteSpace(request.ManifestId))
                throw new InvalidDataException("ManifestId is required.");
            if (request.Version < 1)
                throw new InvalidDataException("Version must be positive.");

            var manifestId = request.ManifestId.Trim();
            ManifestDocument? document = null;
            if (status == "COMPLETED")
            {
                if (request.ItemCount != 0 || request.Items is { Count: > 0 })
                    throw new InvalidDataException("A COMPLETED manifest must have itemCount 0 and no items.");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(request.TransportId))
                    throw new InvalidDataException("TransportId is required for an ACTIVE manifest.");
                if (request.ExpiresAt is null)
                    throw new InvalidDataException("ExpiresAt is required for an ACTIVE manifest.");

                var items = (request.Items ?? []).Select(x => new ManifestItem(x.InventoryId, x.Epc)).ToArray();
                document = new ManifestDocument(
                    manifestId, request.TransportId.Trim(), request.Version, status,
                    request.ItemCount, request.Checksum, null, clock.UtcNow, request.ExpiresAt.Value, items);
                ManifestValidator.Validate(document);
            }

            var current = await repository.GetAuthorizationStateAsync(cancellationToken);
            if (current.Version is long currentVersion && request.Version < currentVersion)
                throw new ManifestVersionConflictException(
                    $"Manifest version {request.Version} is older than applied version {current.Version}.", current.Version);
            if (request.Version == current.Version)
            {
                var sameStatus = status == "ACTIVE" && current.State == AuthorizationState.Active ||
                    status == "COMPLETED" && current.State == AuthorizationState.Completed;
                var sameManifest = string.Equals(manifestId, current.ManifestId, StringComparison.Ordinal);
                if (sameStatus && sameManifest && document is not null)
                {
                    var active = await repository.GetActiveAsync(cancellationToken);
                    sameManifest = active is not null && IsSameManifest(active, document);
                }
                if (!sameStatus || !sameManifest)
                    throw new ManifestVersionConflictException(
                        $"Manifest version {request.Version} is already used by a different manifest or status.", current.Version);

                return Result(request, status, applied: false, alreadyApplied: true);
            }

            if (status == "COMPLETED" && current.State == AuthorizationState.Active &&
                !string.Equals(manifestId, current.ManifestId, StringComparison.Ordinal))
                throw new ManifestVersionConflictException(
                    $"Cannot complete manifest {manifestId}; active manifest is {current.ManifestId}.", current.Version);

            if (status == "COMPLETED")
            {
                await repository.DeactivateAsync(new ManifestCompletion(manifestId, request.Version), cancellationToken);
                validation.Deactivate();
                state.AuthorizationState = AuthorizationState.Completed;
                state.LatestManifestVersion = request.Version;
                state.ActiveManifestId = null;
                state.ActiveManifestVersion = null;
                state.ManifestExpiresAt = null;
            }
            else
            {
                await repository.StageAndActivateAsync(document!, cancellationToken);
                var snapshot = await repository.GetActiveAsync(cancellationToken)
                    ?? throw new InvalidOperationException("Applied manifest could not be restored.");
                validation.Activate(snapshot);
                state.AuthorizationState = AuthorizationState.Active;
                state.LatestManifestVersion = snapshot.Version;
                state.ActiveManifestId = snapshot.ManifestId;
                state.ActiveManifestVersion = snapshot.Version;
                state.ManifestExpiresAt = snapshot.ExpiresAt;
            }

            state.LastManifestSync = clock.UtcNow;
            state.LastManifestError = null;
            logger.LogInformation("Applied pushed manifest {ManifestId} version {Version} with status {Status}",
                request.ManifestId, request.Version, status);
            return Result(request, status, applied: true, alreadyApplied: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is not InvalidDataException and not ManifestVersionConflictException)
                state.LastManifestError = ex.Message;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private ManifestPushResult Result(ManifestPushRequest request, string status, bool applied, bool alreadyApplied) =>
        new(options.Id, request.ManifestId.Trim(), request.Version, status, applied, alreadyApplied, clock.UtcNow);

    private static bool IsSameManifest(ManifestSnapshot active, ManifestDocument candidate) =>
        string.Equals(active.ManifestId, candidate.ManifestId, StringComparison.Ordinal) &&
        string.Equals(active.TransportId, candidate.TransportId, StringComparison.Ordinal) &&
        active.Version == candidate.Version &&
        Math.Abs((active.ExpiresAt.ToUniversalTime() - candidate.ExpiresAt.ToUniversalTime()).TotalMilliseconds) < 1 &&
        active.Epcs.SetEquals(candidate.Items.Select(x => EpcNormalizer.Normalize(x.Epc)));
}
