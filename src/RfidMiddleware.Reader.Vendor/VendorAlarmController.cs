using RfidMiddleware.Core;
using RfidMiddleware.Reader;

namespace RfidMiddleware.Reader.Vendor;

public sealed class VendorAlarmController(VendorReaderAdapter reader, IClock clock, VendorReaderOptions options) : IAlarmController, IAsyncDisposable
{
    private const int DigitalOutput1 = 1;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _pulseCancellation;
    private Task _pulseTask = Task.CompletedTask;
    private AlarmHealth _health = new(true, false, null, clock.UtcNow);
    public AlarmHealth Health => Volatile.Read(ref _health);

    public ValueTask SignalUnauthorizedAsync(CancellationToken cancellationToken = default) => PulseAsync(cancellationToken);

    public async ValueTask ResetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CancelPulse();
            SetAlarm(active: false);
            Volatile.Write(ref _health, new(true, false, null, clock.UtcNow));
        }
        finally { _gate.Release(); }
    }

    private async ValueTask PulseAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CancelPulse();
            var pulse = new CancellationTokenSource();
            _pulseCancellation = pulse;
            _pulseTask = TurnOffAfterPulseAsync(pulse);
            SetAlarm(active: true);
            Volatile.Write(ref _health, new(true, true, null, clock.UtcNow));
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _health, new(false, true, ex.Message, clock.UtcNow));
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task TurnOffAfterPulseAsync(CancellationTokenSource pulse)
    {
        try
        {
            await Task.Delay(options.AlarmPulseMilliseconds, pulse.Token).ConfigureAwait(false);
            await _gate.WaitAsync(pulse.Token).ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(_pulseCancellation, pulse)) return;
                _pulseCancellation = null;
                SetAlarm(active: false);
                Volatile.Write(ref _health, new(true, false, null, clock.UtcNow));
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Volatile.Write(ref _health, new(false, false, ex.Message, clock.UtcNow));
        }
        finally
        {
            pulse.Dispose();
        }
    }

    private void CancelPulse()
    {
        var pulse = Interlocked.Exchange(ref _pulseCancellation, null);
        if (pulse is null) return;
        try { pulse.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void SetAlarm(bool active)
    {
        var failures = new List<string>();
        switch (options.AlarmMode)
        {
            case VendorAlarmMode.Buzzer:
                TrySetBuzzer(active, failures);
                break;
            case VendorAlarmMode.DigitalOutput1:
                TrySetDigitalOutput(DigitalOutput1, active, failures);
                break;
            case VendorAlarmMode.BuzzerAndDigitalOutput1:
                TrySetBuzzer(active, failures);
                TrySetDigitalOutput(DigitalOutput1, active, failures);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(options.AlarmMode), options.AlarmMode,
                    "Unsupported vendor alarm mode.");
        }

        if (failures.Count > 0)
            throw new InvalidOperationException($"RFID SDK alarm channel failures: {string.Join("; ", failures)}.");
    }

    private void TrySetBuzzer(bool active, ICollection<string> failures) => TrySetChannel(
        reader.SetBuzz(active),
        $"reader buzzer {(active ? "on" : "off")}", failures);

    private void TrySetDigitalOutput(int output, bool active, ICollection<string> failures) => TrySetChannel(
        reader.SetDigitalOutput(output, active),
        $"digital output {output} {(active ? "on" : "off")}", failures);

    private static void TrySetChannel(int result, string operation, ICollection<string> failures)
    {
        if (result != 0) failures.Add($"{operation} failed with code {result}");
    }

    public async ValueTask DisposeAsync()
    {
        CancelPulse();
        try { await _pulseTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _gate.Dispose();
    }
}
