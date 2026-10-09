using System.Threading.Channels;
using RfidMiddleware.Core;
using RfidMiddleware.Reader;

namespace RfidMiddleware.Host;

public sealed class ReaderPipelineWorker(
    ILogger<ReaderPipelineWorker> logger,
    MiddlewareOptions options,
    IReaderAdapter reader,
    IAlarmController alarm,
    IEventRepository events,
    ValidationEngine validation,
    IClock clock,
    RuntimeState state) : BackgroundService
{
    private readonly Channel<RawTagRead> _channel = Channel.CreateBounded<RawTagRead>(new BoundedChannelOptions(10_000)
    {
        SingleReader = true,
        SingleWriter = false,
        // TryWrite must fail visibly when the bounded buffer is full. DropWrite
        // can report success while discarding the new read.
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly Channel<ScanEvent> _eventChannel = Channel.CreateBounded<ScanEvent>(new BoundedChannelOptions(10_000)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly Dictionary<string, DateTimeOffset> _alarmCooldown = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var aggregator = new ObservationAggregator(TimeSpan.FromMilliseconds(options.DedupWindowMilliseconds));
        var persistenceTask = PersistLoopAsync();
        try
        {
            await reader.ConnectAsync(stoppingToken);
            await reader.StartInventoryAsync(OnRead, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (persistenceTask.IsCompleted) await persistenceTask;
                var batch = new List<ScanEvent>(256);
                while (_channel.Reader.TryRead(out var read))
                {
                    await AlarmIfRequiredAsync(read.Epc, read.ObservedAt, stoppingToken);
                    foreach (var observation in aggregator.Add(read, validation.Snapshot?.Version))
                        batch.Add(CreateEvent(observation));
                }

                foreach (var observation in aggregator.FlushExpired(clock.UtcNow))
                    batch.Add(CreateEvent(observation));
                if (batch.Count > 0) await EnqueueBatchAsync(batch, stoppingToken);
                await Task.Delay(5, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Reader pipeline failed");
            await SafeSystemAlarmAsync(stoppingToken);
            throw;
        }
        finally
        {
            // Stop callbacks before draining so shutdown has a finite, stable set
            // of reads and follows the vendor SDK's required teardown order.
            await reader.StopInventoryAsync(CancellationToken.None);
            var finalBatch = new List<ScanEvent>();
            while (_channel.Reader.TryRead(out var finalRead))
            {
                foreach (var observation in aggregator.Add(finalRead, validation.Snapshot?.Version))
                {
                    try { finalBatch.Add(CreateEvent(observation)); }
                    catch (Exception ex) { logger.LogError(ex, "Failed to flush final observation"); }
                }
            }
            foreach (var observation in aggregator.FlushExpired(clock.UtcNow, true))
            {
                try { finalBatch.Add(CreateEvent(observation)); }
                catch (Exception ex) { logger.LogError(ex, "Failed to flush final observation"); }
            }
            try { await EnqueueBatchAsync(finalBatch, CancellationToken.None); }
            catch (Exception ex) { logger.LogError(ex, "Failed to enqueue final observation batch"); }
            _eventChannel.Writer.TryComplete();
            try { await persistenceTask; }
            catch (Exception ex) { logger.LogCritical(ex, "Persistence worker failed during shutdown"); }
            await reader.DisconnectAsync(CancellationToken.None);
        }

        bool OnRead(RawTagRead read)
        {
            state.RecordRead(read);
            if (_channel.Writer.TryWrite(read)) return true;
            state.RecordDrop();
            _ = Task.Run(() => SafeSystemAlarmAsync(CancellationToken.None));
            return false;
        }
    }

    private ScanEvent CreateEvent(LogicalObservation observation)
    {
        var decision = validation.ValidateForVersion(observation.Epc, observation.LastSeenAt, observation.ManifestVersion);
        return new ScanEvent(UlidGenerator.NewUlid(clock.UtcNow), decision.Manifest?.ManifestId,
            decision.Manifest?.Version, string.IsNullOrEmpty(decision.NormalizedEpc) ? observation.Epc : decision.NormalizedEpc,
            decision.Outcome, observation.FirstSeenAt, observation.LastSeenAt, observation.ReadCount,
            observation.ReaderId, observation.AntennaIds, observation.StrongestRssi,
            EventSyncState.LocalOnly);
    }

    private async Task AlarmIfRequiredAsync(string epc, DateTimeOffset observedAt, CancellationToken token)
    {
        var decision = validation.Validate(epc, observedAt);
        var key = string.IsNullOrEmpty(decision.NormalizedEpc) ? epc : decision.NormalizedEpc;
        if (decision.Outcome != ValidationOutcome.Authorized && ShouldAlarm(key, observedAt))
        {
            await alarm.SignalUnauthorizedAsync(token);
            state.RecordAlarmLatency(observedAt, clock.UtcNow);
        }
    }

    private async Task PersistAsync(IReadOnlyList<ScanEvent> batch, CancellationToken token)
    {
        try { await events.AppendBatchAsync(batch, token); }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Failed to persist RFID event batch of {Count}", batch.Count);
            await SafeSystemAlarmAsync(token);
            throw;
        }
    }

    private async Task EnqueueBatchAsync(IReadOnlyList<ScanEvent> batch, CancellationToken token)
    {
        foreach (var scanEvent in batch)
        {
            if (_eventChannel.Writer.TryWrite(scanEvent)) continue;
            state.RecordDrop();
            await SafeSystemAlarmAsync(token);
            throw new InvalidOperationException("Durable event channel overflowed.");
        }
    }

    private async Task PersistLoopAsync()
    {
        while (await _eventChannel.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            // Allow a compact transaction batch to accumulate without delaying
            // raw-read validation or alarm actuation.
            await Task.Delay(20).ConfigureAwait(false);
            var batch = new List<ScanEvent>(512);
            while (batch.Count < 512 && _eventChannel.Reader.TryRead(out var scanEvent))
                batch.Add(scanEvent);
            if (batch.Count > 0) await PersistAsync(batch, CancellationToken.None);
        }
    }

    private bool ShouldAlarm(string epc, DateTimeOffset now)
    {
        if (_alarmCooldown.TryGetValue(epc, out var until) && now < until) return false;
        _alarmCooldown[epc] = now.AddSeconds(3);
        if (_alarmCooldown.Count > 10_000)
            foreach (var key in _alarmCooldown.Where(x => x.Value < now).Select(x => x.Key).ToArray()) _alarmCooldown.Remove(key);
        return true;
    }

    private async Task SafeSystemAlarmAsync(CancellationToken token)
    {
        try { await alarm.SignalSystemFaultAsync(token); }
        catch (Exception ex) { logger.LogError(ex, "System alarm output failed"); }
    }
}
