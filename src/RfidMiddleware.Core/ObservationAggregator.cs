namespace RfidMiddleware.Core;

public sealed class ObservationAggregator(TimeSpan window)
{
    private readonly Dictionary<(string Epc, long? Version), MutableObservation> _active = new();

    public IReadOnlyList<LogicalObservation> Add(RawTagRead read, long? manifestVersion)
    {
        var completed = FlushExpired(read.ObservedAt);
        if (!EpcNormalizer.TryNormalize(read.Epc, out var epc))
        {
            completed.Add(new LogicalObservation(read.Epc, manifestVersion, read.ObservedAt, read.ObservedAt,
                1, new HashSet<int> { read.AntennaId }, read.Rssi, read.ReaderId));
            return completed;
        }

        var key = (epc, manifestVersion);
        if (_active.TryGetValue(key, out var current))
        {
            current.LastSeenAt = read.ObservedAt;
            current.ReadCount++;
            current.AntennaIds.Add(read.AntennaId);
            current.StrongestRssi = Math.Max(current.StrongestRssi, read.Rssi);
        }
        else
        {
            _active[key] = new MutableObservation(epc, manifestVersion, read);
        }

        return completed;
    }

    public List<LogicalObservation> FlushExpired(DateTimeOffset now, bool all = false)
    {
        var result = new List<LogicalObservation>();
        foreach (var pair in _active.ToArray())
        {
            if (!all && now - pair.Value.LastSeenAt < window) continue;
            result.Add(pair.Value.ToImmutable());
            _active.Remove(pair.Key);
        }
        return result;
    }

    private sealed class MutableObservation(string epc, long? version, RawTagRead read)
    {
        public string Epc { get; } = epc;
        public long? Version { get; } = version;
        public DateTimeOffset FirstSeenAt { get; } = read.ObservedAt;
        public DateTimeOffset LastSeenAt { get; set; } = read.ObservedAt;
        public int ReadCount { get; set; } = 1;
        public HashSet<int> AntennaIds { get; } = [read.AntennaId];
        public int StrongestRssi { get; set; } = read.Rssi;
        public string ReaderId { get; } = read.ReaderId;

        public LogicalObservation ToImmutable() => new(Epc, Version, FirstSeenAt, LastSeenAt, ReadCount,
            AntennaIds.ToHashSet(), StrongestRssi, ReaderId);
    }
}

