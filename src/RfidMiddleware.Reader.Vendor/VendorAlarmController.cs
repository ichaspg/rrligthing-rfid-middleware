using RfidMiddleware.Core;
using RfidMiddleware.Reader;

namespace RfidMiddleware.Reader.Vendor;

public sealed class VendorAlarmController(VendorReaderAdapter reader, IClock clock, VendorReaderOptions options) : IAlarmController, IAsyncDisposable
{
    private const int AlarmDigitalOutput = 0;
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
            EnsureSuccess(NativeMethods.SetRelayStatus(reader.Handle, AlarmDigitalOutput, NativeRelayStatus.Closed),
                "digital output 0 off");
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
            EnsureSuccess(NativeMethods.SetRelayStatus(reader.Handle, AlarmDigitalOutput, NativeRelayStatus.Open),
                "digital output 0 on");
            Volatile.Write(ref _health, new(true, true, null, clock.UtcNow));
            var pulse = new CancellationTokenSource();
            _pulseCancellation = pulse;
            _pulseTask = TurnOffAfterPulseAsync(pulse);
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
                EnsureSuccess(NativeMethods.SetRelayStatus(reader.Handle, AlarmDigitalOutput, NativeRelayStatus.Closed),
                    "digital output 0 off");
                Volatile.Write(ref _health, new(true, false, null, clock.UtcNow));
                _pulseCancellation = null;
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Volatile.Write(ref _health, new(false, true, ex.Message, clock.UtcNow));
        }
        finally
        {
            pulse.Dispose();
        }
    }

    private void CancelPulse()
    {
        _pulseCancellation?.Cancel();
        _pulseCancellation = null;
    }

    private static void EnsureSuccess(int result, string operation)
    {
        if (result != 0) throw new InvalidOperationException($"RFID SDK {operation} failed with code {result}.");
    }

    public async ValueTask DisposeAsync()
    {
        CancelPulse();
        try { await _pulseTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _gate.Dispose();
    }
}
