using System.Threading.Channels;
using RfidMiddleware.Core;

namespace RfidMiddleware.Host;

public interface IScanSessionRecorder
{
    void Record(RawTagRead read, ValidationDecision decision);
}

public sealed record ScanSessionStreamEvent(string Type, long Sequence, object Data);

public sealed class ScanSessionSubscription(
    ChannelReader<ScanSessionStreamEvent> reader, Action dispose) : IAsyncDisposable
{
    public ChannelReader<ScanSessionStreamEvent> Reader { get; } = reader;
    public ValueTask DisposeAsync()
    {
        dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class ScanSessionService(
    IScanSessionRepository repository,
    IClock clock,
    RuntimeState runtimeState,
    ILogger<ScanSessionService> logger) : BackgroundService, IScanSessionRecorder
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Channel<PersistenceCommand> _persistence = Channel.CreateBounded<PersistenceCommand>(
        new BoundedChannelOptions(10_000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly Dictionary<Guid, Channel<ScanSessionStreamEvent>> _subscribers = [];
    private readonly HashSet<string> _epcs = new(StringComparer.Ordinal);
    private ScanSessionSnapshot? _active;
    private DateTimeOffset _nextCountsAt = DateTimeOffset.MinValue;
    private bool _paused;
    private bool _initialized;

    public async Task InitializeAsync(CancellationToken token = default)
    {
        await repository.DeleteCompletedSessionsBeforeAsync(clock.UtcNow.AddDays(-7), token).ConfigureAwait(false);
        var active = await repository.GetActiveSessionAsync(token).ConfigureAwait(false);
        IReadOnlySet<string> epcs = active is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : await repository.GetSessionEpcsAsync(active.SessionId, token).ConfigureAwait(false);
        lock (_gate)
        {
            _active = active;
            _epcs.Clear();
            foreach (var epc in epcs) _epcs.Add(epc);
            _initialized = true;
        }
    }

    public async Task<ScanSessionStartResult> StartSessionAsync(CancellationToken token = default)
    {
        EnsureInitialized();
        await _lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_active is not null) return new(_active, false);
            }
            var result = await repository.StartOrGetActiveAsync(UlidGenerator.NewUlid(clock.UtcNow), clock.UtcNow, token)
                .ConfigureAwait(false);
            var epcs = await repository.GetSessionEpcsAsync(result.Session.SessionId, token).ConfigureAwait(false);
            lock (_gate)
            {
                _active = result.Session;
                _epcs.Clear();
                foreach (var epc in epcs) _epcs.Add(epc);
                _paused = false;
                _nextCountsAt = DateTimeOffset.MinValue;
            }
            return result;
        }
        finally { _lifecycle.Release(); }
    }

    public ScanSessionSnapshot? GetActive()
    {
        lock (_gate) return _active;
    }

    public Task<ScanSessionSnapshot?> GetSessionAsync(string sessionId, CancellationToken token = default) =>
        repository.GetSessionAsync(sessionId, token);

    public Task<ScanSessionItemsPage?> GetItemsAsync(
        string sessionId, int limit, string? afterEpc, CancellationToken token = default) =>
        repository.GetSessionItemsPageAsync(sessionId, limit, afterEpc, token);

    public ScanSessionSubscription? Subscribe(string sessionId)
    {
        lock (_gate)
        {
            if (_active is null || !string.Equals(_active.SessionId, sessionId, StringComparison.Ordinal)) return null;
            var id = Guid.NewGuid();
            var channel = Channel.CreateBounded<ScanSessionStreamEvent>(new BoundedChannelOptions(1024)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
            _subscribers[id] = channel;
            channel.Writer.TryWrite(CreateEvent("snapshot", _active.LastSequence, SessionPayload(_active)));
            return new(channel.Reader, () => RemoveSubscriber(id));
        }
    }

    public void Record(RawTagRead read, ValidationDecision decision)
    {
        ScanSessionStreamEvent? tagEvent = null;
        ScanSessionStreamEvent? countsEvent = null;
        var overflowed = false;
        lock (_gate)
        {
            if (_active is null || _paused) return;
            var epc = string.IsNullOrEmpty(decision.NormalizedEpc) ? read.Epc.Trim() : decision.NormalizedEpc;
            var isNew = _epcs.Add(epc);
            var sequence = _active.LastSequence + 1;
            var rawCount = _active.RawReadCount + 1;
            var uniqueCount = _epcs.Count;
            _active = _active with
            {
                RawReadCount = rawCount,
                UniqueEpcCount = uniqueCount,
                LastSequence = sequence,
                UpdatedAt = read.ObservedAt
            };
            var observation = new ScanSessionObservation(
                _active.SessionId, _active.Generation, sequence, epc, decision.Outcome,
                read.AntennaId, read.Rssi, read.ObservedAt, rawCount, uniqueCount, isNew);
            // Enqueue before releasing the state lock so reset/complete cannot
            // place their flush marker ahead of an already-counted observation.
            overflowed = !_persistence.Writer.TryWrite(new ObservationCommand(observation));
            if (isNew)
                tagEvent = CreateEvent("tag", sequence, new
                {
                    sessionId = _active.SessionId,
                    generation = _active.Generation,
                    epc,
                    authorizationOutcome = decision.Outcome.ToString(),
                    antennaId = read.AntennaId,
                    rssi = read.Rssi,
                    timestamp = read.ObservedAt,
                    sequence,
                    rawReadCount = rawCount,
                    uniqueEpcCount = uniqueCount
                });
            if (read.ObservedAt >= _nextCountsAt)
            {
                _nextCountsAt = read.ObservedAt.AddMilliseconds(250);
                countsEvent = CreateEvent("counts", sequence, CountsPayload(_active));
            }
            if (tagEvent is not null) Publish(tagEvent);
            if (countsEvent is not null) Publish(countsEvent);
        }

        if (overflowed) MarkPersistenceFault("Scan-session persistence queue overflowed.");
    }

    public async Task<ScanSessionSnapshot> ResetAsync(string sessionId, CancellationToken token = default)
    {
        await _lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ScanSessionSnapshot? active;
            lock (_gate)
            {
                active = _active;
                if (active is not null && string.Equals(active.SessionId, sessionId, StringComparison.Ordinal))
                    _paused = true;
            }
            if (active is null || !string.Equals(active.SessionId, sessionId, StringComparison.Ordinal))
            {
                var existing = await repository.GetSessionAsync(sessionId, token).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException($"Scan session '{sessionId}' was not found.");
                if (existing.Status == ScanSessionStatus.Completed)
                    throw new InvalidOperationException("A completed scan session cannot be reset.");
                throw new InvalidOperationException("The scan session is not active.");
            }
            await FlushAsync(token).ConfigureAwait(false);
            var reset = await repository.ResetSessionAsync(sessionId, clock.UtcNow, token).ConfigureAwait(false);
            lock (_gate)
            {
                _active = reset;
                _epcs.Clear();
                _nextCountsAt = DateTimeOffset.MinValue;
                Publish(CreateEvent("reset", reset.LastSequence, SessionPayload(reset)));
                _paused = false;
            }
            return reset;
        }
        finally
        {
            lock (_gate) _paused = false;
            _lifecycle.Release();
        }
    }

    public async Task<ScanSessionSnapshot> CompleteAsync(string sessionId, CancellationToken token = default)
    {
        await _lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ScanSessionSnapshot? active;
            lock (_gate)
            {
                active = _active;
                if (active is not null && string.Equals(active.SessionId, sessionId, StringComparison.Ordinal)) _paused = true;
            }
            if (active is null || !string.Equals(active.SessionId, sessionId, StringComparison.Ordinal))
            {
                var existing = await repository.GetSessionAsync(sessionId, token).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException($"Scan session '{sessionId}' was not found.");
                if (existing.Status == ScanSessionStatus.Completed) return existing;
                throw new InvalidOperationException("The scan session is not active.");
            }
            await FlushAsync(token).ConfigureAwait(false);
            var completed = await repository.CompleteSessionAsync(sessionId, clock.UtcNow, token).ConfigureAwait(false);
            lock (_gate)
            {
                var message = CreateEvent("completed", completed.LastSequence, SessionPayload(completed));
                foreach (var subscriber in _subscribers.Values)
                {
                    subscriber.Writer.TryWrite(message);
                    subscriber.Writer.TryComplete();
                }
                _subscribers.Clear();
                _active = null;
                _epcs.Clear();
                _paused = false;
            }
            return completed;
        }
        finally
        {
            lock (_gate) _paused = false;
            _lifecycle.Release();
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(PersistLoopAsync(stoppingToken), CleanupLoopAsync(stoppingToken));

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try { await FlushAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { logger.LogError(ex, "Failed to flush scan session during shutdown"); }
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistLoopAsync(CancellationToken token)
    {
        var batch = new List<ScanSessionObservation>(512);
        try
        {
            while (await _persistence.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                await Task.Delay(25, token).ConfigureAwait(false);
                while (batch.Count < 512 && _persistence.Reader.TryRead(out var command))
                {
                    if (command is ObservationCommand observation) batch.Add(observation.Observation);
                    else if (command is FlushCommand flush)
                    {
                        try
                        {
                            await PersistBatchAsync(batch).ConfigureAwait(false);
                            flush.Completion.TrySetResult();
                        }
                        catch (Exception ex) { flush.Completion.TrySetException(ex); }
                    }
                }
                try { await PersistBatchAsync(batch).ConfigureAwait(false); }
                catch { /* Fault is recorded; keep draining so the reader callback remains independent. */ }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            MarkPersistenceFault(ex.Message);
            logger.LogCritical(ex, "Scan-session persistence worker failed");
        }
    }

    private async Task PersistBatchAsync(List<ScanSessionObservation> batch)
    {
        if (batch.Count == 0) return;
        var copy = batch.ToArray();
        batch.Clear();
        try { await repository.ApplySessionObservationsAsync(copy, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex)
        {
            MarkPersistenceFault(ex.Message);
            logger.LogCritical(ex, "Failed to persist {Count} scan-session observations", copy.Length);
            throw;
        }
    }

    private async Task FlushAsync(CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _persistence.Writer.WriteAsync(new FlushCommand(completion), token).ConfigureAwait(false);
        await completion.Task.WaitAsync(token).ConfigureAwait(false);
    }

    private async Task CleanupLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromDays(1), token).ConfigureAwait(false);
                await repository.DeleteCompletedSessionsBeforeAsync(clock.UtcNow.AddDays(-7), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                MarkPersistenceFault(ex.Message);
                logger.LogError(ex, "Scan-session cleanup failed");
            }
        }
    }

    private void Publish(ScanSessionStreamEvent message)
    {
        List<Guid>? slow = null;
        lock (_gate)
        {
            foreach (var (id, subscriber) in _subscribers)
            {
                if (!subscriber.Writer.TryWrite(message)) (slow ??= []).Add(id);
            }
            if (slow is not null)
                foreach (var id in slow)
                {
                    _subscribers[id].Writer.TryComplete();
                    _subscribers.Remove(id);
                }
        }
    }

    private void RemoveSubscriber(Guid id)
    {
        lock (_gate)
            if (_subscribers.Remove(id, out var channel)) channel.Writer.TryComplete();
    }

    private static ScanSessionStreamEvent CreateEvent(string type, long sequence, object data) =>
        new(type, sequence, data);

    private static object SessionPayload(ScanSessionSnapshot session) => new
    {
        sessionId = session.SessionId,
        status = session.Status.ToString(),
        session.Generation,
        session.StartedAt,
        session.ResetAt,
        session.CompletedAt,
        session.RawReadCount,
        session.UniqueEpcCount,
        session.LastSequence,
        session.UpdatedAt
    };

    private static object CountsPayload(ScanSessionSnapshot session) => new
    {
        sessionId = session.SessionId,
        session.Generation,
        session.RawReadCount,
        session.UniqueEpcCount,
        session.LastSequence
    };

    private void MarkPersistenceFault(string message) => runtimeState.LastSystemError = message;
    private void EnsureInitialized()
    {
        lock (_gate)
            if (!_initialized) throw new InvalidOperationException("Scan sessions are not initialized.");
    }

    private abstract record PersistenceCommand;
    private sealed record ObservationCommand(ScanSessionObservation Observation) : PersistenceCommand;
    private sealed record FlushCommand(TaskCompletionSource Completion) : PersistenceCommand;
}
