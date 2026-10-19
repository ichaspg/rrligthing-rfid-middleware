using System.Collections.Concurrent;
using System.Collections.Frozen;
using Microsoft.Extensions.Logging.Abstractions;
using RfidMiddleware.Core;
using RfidMiddleware.Host;
using RfidMiddleware.Reader;

namespace RfidMiddleware.Tests;

public sealed class ReaderPipelineResilienceTests
{
    [Fact]
    public async Task AlarmFailureDoesNotStopOrDiscardUnauthorizedScan()
    {
        var clock = new SystemClock();
        var reader = new TestReader(clock);
        var alarm = new FailingAlarm(clock);
        var events = new TestEventRepository();
        var validation = new ValidationEngine();
        validation.Activate(new ManifestSnapshot(
            "TEST-MANIFEST", "TEST-TRANSPORT", 1, clock.UtcNow, clock.UtcNow.AddMinutes(5),
            new[] { "E28069950000400E45B4F53E" }.ToFrozenSet(StringComparer.Ordinal)));
        var state = new RuntimeState();
        var worker = new ReaderPipelineWorker(
            NullLogger<ReaderPipelineWorker>.Instance,
            new MiddlewareOptions { DedupWindowMilliseconds = 100 },
            reader, alarm, events, validation, clock, state);

        await worker.StartAsync(CancellationToken.None);
        reader.Emit("E20042042AE06416015CFC96");

        await WaitUntilAsync(() => events.Events.Count == 1, TimeSpan.FromSeconds(3));

        Assert.Equal(ValidationOutcome.Unauthorized, events.Events.Single().Result);
        Assert.Equal("relay unsupported", state.LastSystemError);
        Assert.True(reader.Health.InventoryRunning);
        await worker.StopAsync(CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Timed out waiting for scan persistence.");
            await Task.Delay(20);
        }
    }

    private sealed class TestReader(IClock clock) : IReaderAdapter
    {
        private Func<RawTagRead, bool>? _callback;
        public ReaderHealth Health { get; private set; } = new(false, false, null, clock.UtcNow);
        public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
        {
            Health = new(true, false, null, clock.UtcNow);
            return ValueTask.CompletedTask;
        }
        public ValueTask StartInventoryAsync(Func<RawTagRead, bool> onRead, CancellationToken cancellationToken = default)
        {
            _callback = onRead;
            Health = new(true, true, null, clock.UtcNow);
            return ValueTask.CompletedTask;
        }
        public void Emit(string epc) => Assert.True(_callback?.Invoke(new(epc, "TEST-READER", 1, -40, clock.UtcNow)));
        public ValueTask StopInventoryAsync(CancellationToken cancellationToken = default)
        {
            Health = new(Health.Connected, false, null, clock.UtcNow);
            return ValueTask.CompletedTask;
        }
        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
        {
            Health = new(false, false, null, clock.UtcNow);
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingAlarm(IClock clock) : IAlarmController
    {
        public AlarmHealth Health { get; private set; } = new(true, false, null, clock.UtcNow);
        public ValueTask SignalUnauthorizedAsync(CancellationToken cancellationToken = default)
        {
            Health = new(false, true, "relay unsupported", clock.UtcNow);
            throw new InvalidOperationException("relay unsupported");
        }
        public ValueTask ResetAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class TestEventRepository : IEventRepository
    {
        internal ConcurrentQueue<ScanEvent> Events { get; } = new();
        public Task AppendAsync(ScanEvent scanEvent, CancellationToken cancellationToken = default)
        {
            Events.Enqueue(scanEvent);
            return Task.CompletedTask;
        }
        public Task AppendBatchAsync(IReadOnlyList<ScanEvent> scanEvents, CancellationToken cancellationToken = default)
        {
            foreach (var scanEvent in scanEvents) Events.Enqueue(scanEvent);
            return Task.CompletedTask;
        }
        public Task<LocalAuditStats> GetStatsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LocalAuditStats(Events.Count, 0, Events.Count, 0, null));
        public Task<string> VerifyAsync(CancellationToken cancellationToken = default) => Task.FromResult("ok");
    }
}
