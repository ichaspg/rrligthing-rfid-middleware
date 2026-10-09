using System.Collections.Concurrent;
using RfidMiddleware.Core;

namespace RfidMiddleware.Host;

public sealed class RuntimeState
{
    private readonly ConcurrentQueue<RecentRead> _recent = new();
    private readonly ConcurrentQueue<double> _alarmLatencies = new();
    private long _readsReceived;
    private long _readsDropped;
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public string? ActiveManifestId { get; set; }
    public long? ActiveManifestVersion { get; set; }
    public DateTimeOffset? ManifestExpiresAt { get; set; }
    public DateTimeOffset? LastManifestSync { get; set; }
    public string? LastManifestError { get; set; }
    public long ReadsReceived => Interlocked.Read(ref _readsReceived);
    public long ReadsDropped => Interlocked.Read(ref _readsDropped);

    public void RecordRead(RawTagRead read)
    {
        Interlocked.Increment(ref _readsReceived);
        _recent.Enqueue(new(read.Epc, read.ReaderId, read.AntennaId, read.Rssi, read.ObservedAt));
        while (_recent.Count > 1000) _recent.TryDequeue(out _);
    }

    public void RecordDrop() => Interlocked.Increment(ref _readsDropped);
    public void RecordAlarmLatency(DateTimeOffset observedAt, DateTimeOffset invokedAt)
    {
        _alarmLatencies.Enqueue(Math.Max(0, (invokedAt - observedAt).TotalMilliseconds));
        while (_alarmLatencies.Count > 10_000) _alarmLatencies.TryDequeue(out _);
    }

    public (int Samples, double? P99Milliseconds) AlarmLatency()
    {
        var values = _alarmLatencies.ToArray();
        if (values.Length == 0) return (0, null);
        Array.Sort(values);
        var index = Math.Clamp((int)Math.Ceiling(values.Length * 0.99) - 1, 0, values.Length - 1);
        return (values.Length, values[index]);
    }
    public IReadOnlyList<RecentRead> RecentReads() => _recent.ToArray();
}

public sealed record RecentRead(string Epc, string ReaderId, int AntennaId, int Rssi, DateTimeOffset Timestamp);

public sealed class ManifestSyncTrigger
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    public void Signal() { if (_signal.CurrentCount == 0) _signal.Release(); }
    public Task WaitAsync(TimeSpan timeout, CancellationToken token) => _signal.WaitAsync(timeout, token);
}
