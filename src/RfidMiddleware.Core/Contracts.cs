namespace RfidMiddleware.Core;

public interface IClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

public interface IManifestRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<ManifestSnapshot?> GetActiveAsync(CancellationToken cancellationToken = default);
    Task<AuthorizationStateSnapshot> GetAuthorizationStateAsync(CancellationToken cancellationToken = default);
    Task StageAndActivateAsync(ManifestDocument manifest, CancellationToken cancellationToken = default);
    Task DeactivateAsync(ManifestCompletion completion, CancellationToken cancellationToken = default);
}

public interface IEventRepository
{
    Task AppendAsync(ScanEvent scanEvent, CancellationToken cancellationToken = default);
    Task AppendBatchAsync(IReadOnlyList<ScanEvent> scanEvents, CancellationToken cancellationToken = default);
    Task<LocalAuditStats> GetStatsAsync(CancellationToken cancellationToken = default);
    Task<string> VerifyAsync(CancellationToken cancellationToken = default);
}

public interface IManifestClient
{
    Task<ManifestFetchResult> GetCurrentAsync(string? etag, CancellationToken cancellationToken = default);
}

public sealed record ManifestFetchResult(
    ManifestFetchDirective Directive,
    ManifestDocument? Manifest,
    ManifestCompletion? Completion,
    string? ETag);
