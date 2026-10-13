using Microsoft.Extensions.Logging;
using RfidMiddleware.Core;

namespace RfidMiddleware.Reader;

public sealed class ManagedAlarmController(ILogger<ManagedAlarmController> logger, IClock clock, int pulseMilliseconds) : IAlarmController, IDisposable
{
    private readonly object _sync = new();
    private CancellationTokenSource? _pulseCancellation;
    private AlarmHealth _health = new(true, false, null, clock.UtcNow);
    public AlarmHealth Health => Volatile.Read(ref _health);

    public ValueTask SignalUnauthorizedAsync(CancellationToken cancellationToken = default)
    {
        logger.LogWarning("Unauthorized RFID observation alarm");
        Volatile.Write(ref _health, new(true, true, null, clock.UtcNow));
        lock (_sync)
        {
            _pulseCancellation?.Cancel();
            var pulse = new CancellationTokenSource();
            _pulseCancellation = pulse;
            _ = ClearPulseAsync(pulse);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask ResetAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _pulseCancellation?.Cancel();
            _pulseCancellation = null;
        }
        Volatile.Write(ref _health, new(true, false, null, clock.UtcNow));
        return ValueTask.CompletedTask;
    }

    private async Task ClearPulseAsync(CancellationTokenSource pulse)
    {
        try
        {
            await Task.Delay(Math.Max(1, pulseMilliseconds), pulse.Token).ConfigureAwait(false);
            lock (_sync)
            {
                if (!ReferenceEquals(_pulseCancellation, pulse)) return;
                _pulseCancellation = null;
                Volatile.Write(ref _health, new(true, false, null, clock.UtcNow));
            }
        }
        catch (OperationCanceledException) { }
        finally { pulse.Dispose(); }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _pulseCancellation?.Cancel();
            _pulseCancellation = null;
        }
    }
}
