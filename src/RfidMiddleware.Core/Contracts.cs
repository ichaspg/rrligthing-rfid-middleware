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
    Task<ManifestItemsPage?> GetActiveItemsPageAsync(
        int limit, string? afterEpc, CancellationToken cancellationToken = default);
}

public interface IEventRepository
{
    Task AppendAsync(ScanEvent scanEvent, CancellationToken cancellationToken = default);
    Task AppendBatchAsync(IReadOnlyList<ScanEvent> scanEvents, CancellationToken cancellationToken = default);
    Task<LocalAuditStats> GetStatsAsync(CancellationToken cancellationToken = default);
    Task<string> VerifyAsync(CancellationToken cancellationToken = default);
}

public interface IScanSessionRepository
{
    Task<ScanSessionStartResult> StartOrGetActiveAsync(
        string candidateSessionId, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<ScanSessionSnapshot?> GetActiveSessionAsync(CancellationToken cancellationToken = default);
    Task<ScanSessionSnapshot?> GetSessionAsync(
        string sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlySet<string>> GetSessionEpcsAsync(
        string sessionId, CancellationToken cancellationToken = default);
    Task ApplySessionObservationsAsync(
        IReadOnlyList<ScanSessionObservation> observations, CancellationToken cancellationToken = default);
    Task<ScanSessionSnapshot> ResetSessionAsync(
        string sessionId, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<ScanSessionSnapshot> CompleteSessionAsync(
        string sessionId, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<ScanSessionItemsPage?> GetSessionItemsPageAsync(
        string sessionId, int limit, string? afterEpc, CancellationToken cancellationToken = default);
    Task<int> DeleteCompletedSessionsBeforeAsync(
        DateTimeOffset cutoff, CancellationToken cancellationToken = default);
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
