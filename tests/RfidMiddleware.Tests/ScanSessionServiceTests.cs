using Microsoft.Extensions.Logging.Abstractions;
using RfidMiddleware.Core;
using RfidMiddleware.Host;

namespace RfidMiddleware.Tests;

public sealed class ScanSessionServiceTests
{
    [Fact]
    public async Task DuplicateReadsIncrementRawCountAndPublishOneTag()
    {
        var repository = new MemoryScanSessionRepository();
        var clock = new SessionClock();
        var service = CreateService(repository, clock);
        await service.InitializeAsync();
        await service.StartAsync(CancellationToken.None);
        try
        {
            var started = await service.StartSessionAsync();
            await using var subscription = service.Subscribe(started.Session.SessionId)!;
            var read = new RawTagRead("E2806915000060068C95C54B", "R1", 2, -45, clock.UtcNow);
            var decision = new ValidationDecision(ValidationOutcome.Authorized, read.Epc, null);

            service.Record(read, decision);
            clock.Advance(TimeSpan.FromMilliseconds(10));
            service.Record(read with { Rssi = -40, ObservedAt = clock.UtcNow }, decision);
            var completed = await service.CompleteAsync(started.Session.SessionId);

            Assert.Equal(2, completed.RawReadCount);
            Assert.Equal(1, completed.UniqueEpcCount);
            var page = await repository.GetSessionItemsPageAsync(started.Session.SessionId, 100, null);
            var item = Assert.Single(page!.Items);
            Assert.Equal(2, item.ReadCount);
            Assert.Equal(-40, item.StrongestRssi);
            Assert.Equal([2], item.AntennaIds);

            var events = new List<ScanSessionStreamEvent>();
            while (subscription.Reader.TryRead(out var message)) events.Add(message);
            Assert.Single(events, x => x.Type == "tag");
            Assert.Contains(events, x => x.Type == "snapshot");
            Assert.Contains(events, x => x.Type == "completed");
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ResetClearsItemsAndCompletedSessionCannotBeReset()
    {
        var repository = new MemoryScanSessionRepository();
        var clock = new SessionClock();
        var service = CreateService(repository, clock);
        await service.InitializeAsync();
        await service.StartAsync(CancellationToken.None);
        try
        {
            var first = await service.StartSessionAsync();
            service.Record(new("BAD", "R1", 1, -60, clock.UtcNow),
                new(ValidationOutcome.InvalidEpc, string.Empty, null));
            var reset = await service.ResetAsync(first.Session.SessionId);
            Assert.Equal(2, reset.Generation);
            Assert.Equal(0, reset.RawReadCount);
            Assert.Equal(0, reset.UniqueEpcCount);
            Assert.Empty((await repository.GetSessionItemsPageAsync(first.Session.SessionId, 100, null))!.Items);

            await service.CompleteAsync(first.Session.SessionId);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResetAsync(first.Session.SessionId));
            var second = await service.StartSessionAsync();
            Assert.True(second.Created);
            Assert.NotEqual(first.Session.SessionId, second.Session.SessionId);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    private static ScanSessionService CreateService(MemoryScanSessionRepository repository, IClock clock) =>
        new(repository, clock, new RuntimeState(), NullLogger<ScanSessionService>.Instance);

    private sealed class SessionClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.Parse("2026-11-11T00:00:00Z");
        public void Advance(TimeSpan duration) => UtcNow += duration;
    }

    private sealed class MemoryScanSessionRepository : IScanSessionRepository
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, ScanSessionSnapshot> _sessions = [];
        private readonly Dictionary<string, Dictionary<string, ScanSessionItem>> _items = [];

        public Task<ScanSessionStartResult> StartOrGetActiveAsync(string candidateSessionId, DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var active = _sessions.Values.SingleOrDefault(x => x.Status == ScanSessionStatus.Active);
                if (active is not null) return Task.FromResult(new ScanSessionStartResult(active, false));
                var created = new ScanSessionSnapshot(candidateSessionId, ScanSessionStatus.Active, 1, now,
                    null, null, 0, 0, 0, now);
                _sessions[candidateSessionId] = created;
                _items[candidateSessionId] = [];
                return Task.FromResult(new ScanSessionStartResult(created, true));
            }
        }

        public Task<ScanSessionSnapshot?> GetActiveSessionAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate) return Task.FromResult(_sessions.Values.SingleOrDefault(x => x.Status == ScanSessionStatus.Active));
        }

        public Task<ScanSessionSnapshot?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            lock (_gate) return Task.FromResult(_sessions.GetValueOrDefault(sessionId));
        }

        public Task<IReadOnlySet<string>> GetSessionEpcsAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
                return Task.FromResult<IReadOnlySet<string>>(_items.TryGetValue(sessionId, out var items)
                    ? new HashSet<string>(items.Keys, StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal));
        }

        public Task ApplySessionObservationsAsync(IReadOnlyList<ScanSessionObservation> observations,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                foreach (var observation in observations)
                {
                    var session = _sessions[observation.SessionId];
                    if (session.Status != ScanSessionStatus.Active || session.Generation != observation.Generation)
                        throw new InvalidOperationException();
                    _sessions[observation.SessionId] = session with
                    {
                        RawReadCount = observation.RawReadCount,
                        UniqueEpcCount = observation.UniqueEpcCount,
                        LastSequence = observation.Sequence,
                        UpdatedAt = observation.ObservedAt
                    };
                    var items = _items[observation.SessionId];
                    if (items.TryGetValue(observation.Epc, out var current))
                    {
                        items[observation.Epc] = current with
                        {
                            LastSeenAt = observation.ObservedAt,
                            ReadCount = current.ReadCount + 1,
                            LatestOutcome = observation.Outcome,
                            AntennaIds = current.AntennaIds.Append(observation.AntennaId).Distinct().Order().ToArray(),
                            StrongestRssi = Math.Max(current.StrongestRssi, observation.Rssi)
                        };
                    }
                    else
                    {
                        items[observation.Epc] = new(observation.Epc, observation.ObservedAt,
                            observation.ObservedAt, 1, observation.Outcome, [observation.AntennaId], observation.Rssi);
                    }
                }
            }
            return Task.CompletedTask;
        }

        public Task<ScanSessionSnapshot> ResetSessionAsync(string sessionId, DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var session = _sessions[sessionId];
                if (session.Status == ScanSessionStatus.Completed) throw new InvalidOperationException();
                var reset = session with
                {
                    Generation = session.Generation + 1,
                    ResetAt = now,
                    RawReadCount = 0,
                    UniqueEpcCount = 0,
                    UpdatedAt = now
                };
                _sessions[sessionId] = reset;
                _items[sessionId].Clear();
                return Task.FromResult(reset);
            }
        }

        public Task<ScanSessionSnapshot> CompleteSessionAsync(string sessionId, DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var current = _sessions[sessionId];
                if (current.Status == ScanSessionStatus.Completed) return Task.FromResult(current);
                var completed = current with { Status = ScanSessionStatus.Completed, CompletedAt = now, UpdatedAt = now };
                _sessions[sessionId] = completed;
                return Task.FromResult(completed);
            }
        }

        public Task<ScanSessionItemsPage?> GetSessionItemsPageAsync(string sessionId, int limit, string? afterEpc,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!_sessions.TryGetValue(sessionId, out var session))
                    return Task.FromResult<ScanSessionItemsPage?>(null);
                var all = _items[sessionId].Values.Where(x => afterEpc is null || string.CompareOrdinal(x.Epc, afterEpc) > 0)
                    .OrderBy(x => x.Epc).ToArray();
                var pageItems = all.Take(limit).ToArray();
                var next = all.Length > limit ? pageItems[^1].Epc : null;
                return Task.FromResult<ScanSessionItemsPage?>(new(sessionId, session.Generation,
                    session.RawReadCount, session.UniqueEpcCount, session.LastSequence, pageItems, next));
            }
        }

        public Task<int> DeleteCompletedSessionsBeforeAsync(DateTimeOffset cutoff,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var ids = _sessions.Values.Where(x => x.Status == ScanSessionStatus.Completed && x.CompletedAt < cutoff)
                    .Select(x => x.SessionId).ToArray();
                foreach (var id in ids) { _sessions.Remove(id); _items.Remove(id); }
                return Task.FromResult(ids.Length);
            }
        }
    }
}
