using System.Collections.Frozen;

namespace RfidMiddleware.Core;

public enum ValidationOutcome
{
    Authorized,
    Unauthorized,
    NoActiveManifest,
    StaleManifest,
    InvalidEpc,
    ReaderError,
    PersistenceError
}

public enum EventSyncState { LocalOnly, Pending, InFlight, Acknowledged, Retry, Rejected, DeadLetter }
public enum ManifestState { Staged, Validated, Active, Superseded, Invalid }

public sealed record ManifestItem(string InventoryId, string Epc);

public sealed record ManifestDocument(
    string ManifestId,
    string TransportId,
    long Version,
    string Status,
    int ItemCount,
    string? Checksum,
    string? ETag,
    DateTimeOffset DownloadedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<ManifestItem> Items);

public sealed record ManifestSnapshot(
    string ManifestId,
    string TransportId,
    long Version,
    DateTimeOffset ActivatedAt,
    DateTimeOffset ExpiresAt,
    FrozenSet<string> Epcs)
{
    public static ManifestSnapshot Create(ManifestDocument document, DateTimeOffset activatedAt) =>
        new(document.ManifestId, document.TransportId, document.Version, activatedAt, document.ExpiresAt,
            document.Items.Select(x => EpcNormalizer.Normalize(x.Epc)).ToFrozenSet(StringComparer.Ordinal));
}

public sealed record RawTagRead(
    string Epc,
    string ReaderId,
    int AntennaId,
    int Rssi,
    DateTimeOffset ObservedAt);

public sealed record LogicalObservation(
    string Epc,
    long? ManifestVersion,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    int ReadCount,
    IReadOnlySet<int> AntennaIds,
    int StrongestRssi,
    string ReaderId);

public sealed record ScanEvent(
    string EventId,
    string? ManifestId,
    long? ManifestVersion,
    string Epc,
    ValidationOutcome Result,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    int ReadCount,
    string ReaderId,
    IReadOnlySet<int> AntennaIds,
    int StrongestRssi,
    EventSyncState SyncState = EventSyncState.Pending,
    int RetryCount = 0,
    DateTimeOffset? NextAttemptAt = null,
    string? LastError = null);

public sealed record EventUploadResult(string EventId, UploadDisposition Disposition, int? HttpStatus = null, string? Error = null);
public enum UploadDisposition { Acknowledged, Retry, Rejected }

public sealed record QueueStats(int Pending, int InFlight, int Retry, int Rejected, int DeadLetter, DateTimeOffset? OldestPending);
public sealed record LocalAuditStats(long Total, long Authorized, long Unauthorized, long Other, DateTimeOffset? NewestObservation);

public sealed record ReaderHealth(bool Connected, bool InventoryRunning, string? Error, DateTimeOffset UpdatedAt);
public sealed record AlarmHealth(bool Healthy, bool Active, string? Error, DateTimeOffset UpdatedAt);
