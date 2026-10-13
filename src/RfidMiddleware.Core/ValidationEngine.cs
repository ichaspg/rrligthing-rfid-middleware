using System.Collections.Concurrent;

namespace RfidMiddleware.Core;

public sealed class ValidationEngine
{
    private ManifestSnapshot? _snapshot;
    private readonly ConcurrentDictionary<long, ManifestSnapshot> _snapshots = new();

    public ManifestSnapshot? Snapshot => Volatile.Read(ref _snapshot);
    public void Activate(ManifestSnapshot snapshot)
    {
        _snapshots[snapshot.Version] = snapshot;
        Volatile.Write(ref _snapshot, snapshot);
        if (_snapshots.Count > 4)
        {
            foreach (var version in _snapshots.Keys.OrderDescending().Skip(4))
                _snapshots.TryRemove(version, out _);
        }
    }

    public void Deactivate() => Volatile.Write(ref _snapshot, null);

    public ValidationDecision Validate(string epc, DateTimeOffset now)
        => ValidateAgainst(epc, now, Snapshot);

    public ValidationDecision ValidateForVersion(string epc, DateTimeOffset now, long? manifestVersion)
    {
        var snapshot = manifestVersion is not null && _snapshots.TryGetValue(manifestVersion.Value, out var versioned)
            ? versioned
            : null;
        return ValidateAgainst(epc, now, snapshot);
    }

    private static ValidationDecision ValidateAgainst(string epc, DateTimeOffset now, ManifestSnapshot? snapshot)
    {
        if (!EpcNormalizer.TryNormalize(epc, out var normalized))
            return new(ValidationOutcome.InvalidEpc, string.Empty, snapshot);

        if (snapshot is null)
            return new(ValidationOutcome.NoActiveManifest, normalized, null);
        if (now >= snapshot.ExpiresAt)
            return new(ValidationOutcome.StaleManifest, normalized, snapshot);

        return new(snapshot.Epcs.Contains(normalized) ? ValidationOutcome.Authorized : ValidationOutcome.Unauthorized,
            normalized, snapshot);
    }
}

public sealed record ValidationDecision(ValidationOutcome Outcome, string NormalizedEpc, ManifestSnapshot? Manifest);
