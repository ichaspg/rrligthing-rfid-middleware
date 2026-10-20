using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using RfidMiddleware.Core;
using RfidMiddleware.Reader;

namespace RfidMiddleware.Reader.Vendor;

public sealed class VendorReaderAdapter : IReaderAdapter
{
    private static VendorReaderAdapter? s_active;
    private readonly ILogger<VendorReaderAdapter> _logger;
    private readonly IClock _clock;
    private readonly VendorReaderOptions _options;
    private readonly string? _lockFile;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private ReaderSafeHandle? _handle;
    private ReaderDeviceLease? _deviceLease;
    private Func<RawTagRead, bool>? _onRead;
    private ReaderHealth _health;

    public VendorReaderAdapter(ILogger<VendorReaderAdapter> logger, IClock clock, VendorReaderOptions options,
        string? lockFile = null)
    {
        _logger = logger;
        _clock = clock;
        _options = options;
        _lockFile = lockFile;
        _health = new(false, false, null, clock.UtcNow);
        NativeMethods.InstallResolver();
    }

    public ReaderHealth Health => Volatile.Read(ref _health);
    internal int Handle => _handle is { IsInvalid: false, IsClosed: false } ? _handle.Value : throw new InvalidOperationException("Reader is disconnected.");

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_handle is { IsInvalid: false, IsClosed: false }) return;
            _deviceLease = ReaderDeviceLease.Acquire(_lockFile);
            var result = _options.Transport switch
            {
                VendorTransport.NativeMock => NativeMethods.ConnectMock(_options.MockPort, out var mockHandle) is var rc
                    ? (rc, mockHandle) : default,
                VendorTransport.Tcp => NativeMethods.ConnectNet(_options.Address, _options.Port, NativeTransport.Tcp,
                    _options.TimeoutMilliseconds, out var tcpHandle) is var rc ? (rc, tcpHandle) : default,
                VendorTransport.Serial => NativeMethods.ConnectSerial(_options.SerialDevice, _options.Baud,
                    _options.Rs485Address, _options.TimeoutMilliseconds, out var serialHandle) is var rc ? (rc, serialHandle) : default,
                _ => throw new ArgumentOutOfRangeException()
            };
            ThrowIfError(result.Item1, "connect");
            _handle = ReaderSafeHandle.Create(result.Item2);
            ApplyConfiguredAntennaPower();
            Volatile.Write(ref _health, new(true, false, null, _clock.UtcNow));
            _logger.LogInformation("Vendor reader connected using {Transport}", _options.Transport);
        }
        catch (Exception ex)
        {
            _handle?.Dispose();
            _handle = null;
            _deviceLease?.Dispose();
            _deviceLease = null;
            Volatile.Write(ref _health, new(false, false, ex.Message, _clock.UtcNow));
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask StartInventoryAsync(Func<RawTagRead, bool> onRead, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        var callbackRegistered = false;
        try
        {
            if (!Health.Connected) throw new InvalidOperationException("Reader is not connected.");
            if (Health.InventoryRunning) return;
            if (Interlocked.CompareExchange(ref s_active, this, null) is not null)
                throw new InvalidOperationException("Only one vendor reader callback may be active in this process.");
            _onRead = onRead;
            ThrowIfError(StartNativeListening(Handle), "start_listen_auto_read");
            callbackRegistered = true;
            ThrowIfError(NativeMethods.SetWorkMode(Handle, NativeWorkMode.Auto), "set_work_mode(auto)");
            Volatile.Write(ref _health, new(true, true, null, _clock.UtcNow));
        }
        catch
        {
            if (callbackRegistered)
                _ = NativeMethods.StopListenAutoRead(Handle);
            _onRead = null;
            Interlocked.CompareExchange(ref s_active, null, this);
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask StopInventoryAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Health.InventoryRunning) return;
            _ = NativeMethods.SetWorkMode(Handle, NativeWorkMode.Command);
            _ = NativeMethods.StopListenAutoRead(Handle);
            _onRead = null;
            Interlocked.CompareExchange(ref s_active, null, this);
            Volatile.Write(ref _health, new(Health.Connected, false, null, _clock.UtcNow));
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await StopInventoryAsync(cancellationToken).ConfigureAwait(false);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _handle?.Dispose();
            _handle = null;
            _deviceLease?.Dispose();
            _deviceLease = null;
            Volatile.Write(ref _health, new(false, false, null, _clock.UtcNow));
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void OnNativeRead(NativeAutoReadOutput output)
    {
        var active = Volatile.Read(ref s_active);
        if (active is null) return;
        try
        {
            string epc;
            string readerId;
            epc = ReadCString(output.Epc, 513);
            readerId = ReadCString(output.Id, 25);
            if (string.IsNullOrWhiteSpace(readerId)) readerId = active._options.ReaderId;
            var timestamp = output.Timestamp > 0
                ? DateTimeOffset.FromUnixTimeSeconds(output.Timestamp)
                : active._clock.UtcNow;
            if (active._onRead?.Invoke(new RawTagRead(epc, readerId, output.Ant, output.Rssi, timestamp)) == false)
                Volatile.Write(ref active._health, new(true, true, "Reader channel overflow", active._clock.UtcNow));
        }
        catch (Exception ex)
        {
            Volatile.Write(ref active._health, new(true, true, ex.Message, active._clock.UtcNow));
        }
    }

    private static unsafe int StartNativeListening(int handle) =>
        NativeMethods.StartListenAutoRead(handle, &OnNativeRead);

    private void ApplyConfiguredAntennaPower()
    {
        if (_options.AntennaPower is not { } power) return;
        if (power is < 1 or > 33)
            throw new InvalidOperationException("VendorReader.AntennaPower must be between 1 and 33 dBm.");

        ThrowIfError(NativeMethods.GetAntennaCount(Handle, out var antennaCount), "get_ant_count");
        if (antennaCount is < 1 or > 16)
            throw new InvalidOperationException($"RFID SDK returned invalid antenna count {antennaCount}.");

        for (var antenna = 1; antenna <= antennaCount; antenna++)
        {
            ThrowIfError(NativeMethods.SetPower(Handle, antenna, power), $"set_power(antenna {antenna})");
            ThrowIfError(NativeMethods.GetPower(Handle, antenna, out var readBack), $"get_power(antenna {antenna})");
            if (readBack != power)
                throw new InvalidOperationException(
                    $"RFID antenna {antenna} power read-back was {readBack} dBm, expected {power} dBm.");
        }

        _logger.LogInformation("Configured {AntennaCount} reader antennas to {Power} dBm", antennaCount, power);
    }

    private static unsafe string ReadCString(byte* pointer, int maximum)
    {
        var length = 0;
        while (length < maximum && pointer[length] != 0) length++;
        return System.Text.Encoding.UTF8.GetString(new ReadOnlySpan<byte>(pointer, length));
    }

    private static void ThrowIfError(int result, string operation)
    {
        if (result != 0) throw new InvalidOperationException($"RFID SDK {operation} failed with code {result}.");
    }
}
