using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RfidMiddleware.Core;
using RfidMiddleware.Reader;

namespace RfidMiddleware.Reader.Vendor;

public sealed class VendorReaderAdapter : IReaderAdapter
{
    private static VendorReaderAdapter? s_active;
    private static VendorReaderAdapter? s_listener;
    private readonly ILogger<VendorReaderAdapter> _logger;
    private readonly IClock _clock;
    private readonly VendorReaderOptions _options;
    private readonly string? _lockFile;
    private readonly IVendorNativeApi _native;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private ReaderSafeHandle? _handle;
    private ReaderDeviceLease? _deviceLease;
    private Func<RawTagRead, bool>? _onRead;
    private ReaderHealth _health;
    private bool _inventoryRequested;
    private bool _listenerStarted;
    private Channel<InboundSignal>? _inboundSignals;
    private Task? _inboundPump;
    private TaskCompletionSource _firstInboundConnection = NewCompletionSource();

    public VendorReaderAdapter(ILogger<VendorReaderAdapter> logger, IClock clock, VendorReaderOptions options,
        string? lockFile = null)
        : this(logger, clock, options, new PInvokeVendorNativeApi(), lockFile) { }

    internal VendorReaderAdapter(ILogger<VendorReaderAdapter> logger, IClock clock, VendorReaderOptions options,
        IVendorNativeApi native, string? lockFile = null)
    {
        _logger = logger;
        _clock = clock;
        _options = options;
        _native = native;
        _lockFile = lockFile;
        _health = new(false, false, null, clock.UtcNow);
        VendorReaderOptionsValidator.Validate(options);
    }

    public ReaderHealth Health => Volatile.Read(ref _health);
    internal int Handle => _handle is { IsInvalid: false, IsClosed: false }
        ? _handle.Value
        : throw new InvalidOperationException("Reader is disconnected.");

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_options.Transport == VendorTransport.TcpListener)
        {
            await ConnectInboundAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_handle is { IsInvalid: false, IsClosed: false }) return;
            _deviceLease = ReaderDeviceLease.Acquire(_lockFile);
            var result = _options.Transport switch
            {
                VendorTransport.NativeMock => _native.ConnectMock(_options.MockPort, out var mockHandle) is var rc
                    ? (rc, mockHandle) : default,
                VendorTransport.Tcp => _native.ConnectNet(_options.Address, _options.Port, NativeTransport.Tcp,
                    _options.TimeoutMilliseconds, out var tcpHandle) is var rc ? (rc, tcpHandle) : default,
                VendorTransport.Serial => _native.ConnectSerial(_options.SerialDevice, _options.Baud,
                    _options.Rs485Address, _options.TimeoutMilliseconds, out var serialHandle) is var rc ? (rc, serialHandle) : default,
                _ => throw new ArgumentOutOfRangeException()
            };
            ThrowIfError(result.Item1, "connect");
            _handle = ReaderSafeHandle.Create(result.Item2, _native.Disconnect);
            ApplyConfiguredAntennaPower(Handle);
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

    private async ValueTask ConnectInboundAsync(CancellationToken cancellationToken)
    {
        Task firstConnection;
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_listenerStarted)
            {
                _deviceLease = ReaderDeviceLease.Acquire(_lockFile);
                if (Interlocked.CompareExchange(ref s_listener, this, null) is not null)
                    throw new InvalidOperationException("Only one inbound vendor reader listener may be active in this process.");

                _firstInboundConnection = NewCompletionSource();
                _inboundSignals = Channel.CreateUnbounded<InboundSignal>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });
                _listenerStarted = true;
                _inboundPump = ProcessInboundSignalsAsync(_inboundSignals.Reader);
                var result = StartInboundListener(_options.Port);
                if (result != 0)
                {
                    _listenerStarted = false;
                    _inboundSignals.Writer.TryComplete();
                    Interlocked.CompareExchange(ref s_listener, null, this);
                    _deviceLease.Dispose();
                    _deviceLease = null;
                    ThrowIfError(result, "listen_vup_tcp_client_mode_reader");
                }

                Volatile.Write(ref _health,
                    new(false, false, $"Waiting for an inbound reader on TCP port {_options.Port}.", _clock.UtcNow));
                _logger.LogInformation("Waiting for one inbound RFID reader on TCP port {Port}", _options.Port);
            }

            firstConnection = _firstInboundConnection.Task;
        }
        catch (Exception ex)
        {
            if (_listenerStarted)
            {
                try { _native.StopListenTcpClientMode(_options.Port); }
                catch (Exception stopException)
                {
                    _logger.LogWarning(stopException, "Failed to stop inbound RFID listener after startup failure");
                }
            }
            _listenerStarted = false;
            _inboundSignals?.Writer.TryComplete();
            Interlocked.CompareExchange(ref s_listener, null, this);
            _deviceLease?.Dispose();
            _deviceLease = null;
            Volatile.Write(ref _health, new(false, false, ex.Message, _clock.UtcNow));
            throw;
        }
        finally { _lifecycle.Release(); }

        await firstConnection.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask StartInventoryAsync(Func<RawTagRead, bool> onRead,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onRead);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _onRead = onRead;
            _inventoryRequested = true;
            if (_handle is null)
            {
                Volatile.Write(ref _health,
                    new(false, false, $"Waiting for an inbound reader on TCP port {_options.Port}.", _clock.UtcNow));
                return;
            }
            if (Health.InventoryRunning) return;
            StartInventoryOnHandle(Handle);
            Volatile.Write(ref _health, new(true, true, null, _clock.UtcNow));
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _health, new(_handle is not null, false, ex.Message, _clock.UtcNow));
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask StopInventoryAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _inventoryRequested = false;
            if (_handle is not null && Health.InventoryRunning)
            {
                _ = _native.SetWorkMode(Handle, NativeWorkMode.Command);
                _ = _native.StopListenAutoRead(Handle);
            }
            _onRead = null;
            Interlocked.CompareExchange(ref s_active, null, this);
            Volatile.Write(ref _health, new(_handle is not null, false, null, _clock.UtcNow));
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await StopInventoryAsync(cancellationToken).ConfigureAwait(false);
        Task? inboundPump;
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_listenerStarted)
            {
                _listenerStarted = false;
                _native.StopListenTcpClientMode(_options.Port);
                Interlocked.CompareExchange(ref s_listener, null, this);
                _inboundSignals?.Writer.TryComplete();
                _firstInboundConnection.TrySetCanceled();
            }
            inboundPump = _inboundPump;
            DetachAndDisposeHandle();
            _deviceLease?.Dispose();
            _deviceLease = null;
            Volatile.Write(ref _health, new(false, false, null, _clock.UtcNow));
        }
        finally { _lifecycle.Release(); }

        if (inboundPump is not null)
            await inboundPump.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    internal int SetBuzz(bool active) =>
        _native.SetBuzz(Handle, active ? NativeBuzzerStatus.On : NativeBuzzerStatus.Off);

    internal int SetDigitalOutput(int output, bool active) =>
        _native.SetRelayStatus(Handle, output, active ? NativeRelayStatus.Open : NativeRelayStatus.Closed);

    private async Task ProcessInboundSignalsAsync(ChannelReader<InboundSignal> reader)
    {
        await foreach (var signal in reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (signal.ConnectedHandle is { } handle)
                    await AcceptInboundConnectionAsync(handle).ConfigureAwait(false);
                else
                    await HandleInboundDisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process inbound RFID reader lifecycle event");
                Volatile.Write(ref _health, new(false, false, ex.Message, _clock.UtcNow));
            }
        }
    }

    private async Task AcceptInboundConnectionAsync(int handle)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_listenerStarted || _handle is { IsInvalid: false, IsClosed: false })
            {
                _native.Disconnect(handle);
                _logger.LogWarning("Rejected an additional inbound RFID reader connection");
                return;
            }

            var accepted = ReaderSafeHandle.Create(handle, _native.Disconnect);
            _handle = accepted;
            try
            {
                SetNativeDisconnectCallback(handle);
                ApplyConfiguredAntennaPower(handle);
                if (_inventoryRequested)
                    StartInventoryOnHandle(handle);
                Volatile.Write(ref _health, new(true, _inventoryRequested, null, _clock.UtcNow));
                _firstInboundConnection.TrySetResult();
                _logger.LogInformation("Accepted inbound RFID reader connection on TCP port {Port}", _options.Port);
            }
            catch
            {
                if (ReferenceEquals(_handle, accepted)) _handle = null;
                ClearNativeDisconnectCallback(handle);
                accepted.Dispose();
                Interlocked.CompareExchange(ref s_active, null, this);
                throw;
            }
        }
        finally { _lifecycle.Release(); }
    }

    private async Task HandleInboundDisconnectAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_handle is null) return;
            Interlocked.CompareExchange(ref s_active, null, this);
            DetachAndDisposeHandle();
            Volatile.Write(ref _health,
                new(false, false, $"Waiting for an inbound reader on TCP port {_options.Port}.", _clock.UtcNow));
            _logger.LogWarning("Inbound RFID reader disconnected; listener remains active on TCP port {Port}",
                _options.Port);
        }
        finally { _lifecycle.Release(); }
    }

    private void StartInventoryOnHandle(int handle)
    {
        if (_onRead is null) throw new InvalidOperationException("Reader observation callback is not configured.");
        var current = Interlocked.CompareExchange(ref s_active, this, null);
        if (current is not null && !ReferenceEquals(current, this))
            throw new InvalidOperationException("Only one vendor reader callback may be active in this process.");

        var callbackRegistered = false;
        try
        {
            ThrowIfError(StartNativeListening(handle), "start_listen_auto_read");
            callbackRegistered = true;
            ThrowIfError(_native.SetWorkMode(handle, NativeWorkMode.Auto), "set_work_mode(auto)");
        }
        catch
        {
            if (callbackRegistered) _ = _native.StopListenAutoRead(handle);
            Interlocked.CompareExchange(ref s_active, null, this);
            throw;
        }
    }

    private void ApplyConfiguredAntennaPower(int handle)
    {
        if (_options.AntennaPower is not { } power) return;
        ThrowIfError(_native.GetAntennaCount(handle, out var antennaCount), "get_ant_count");
        if (antennaCount is < 1 or > 16)
            throw new InvalidOperationException($"RFID SDK returned invalid antenna count {antennaCount}.");

        for (var antenna = 1; antenna <= antennaCount; antenna++)
        {
            ThrowIfError(_native.SetPower(handle, antenna, power), $"set_power(antenna {antenna})");
            ThrowIfError(_native.GetPower(handle, antenna, out var readBack), $"get_power(antenna {antenna})");
            if (readBack != power)
                throw new InvalidOperationException(
                    $"RFID antenna {antenna} power read-back was {readBack} dBm, expected {power} dBm.");
        }

        _logger.LogInformation("Configured {AntennaCount} reader antennas to {Power} dBm", antennaCount, power);
    }

    private void DetachAndDisposeHandle()
    {
        var handle = _handle;
        _handle = null;
        if (handle is null) return;
        if (!handle.IsClosed && !handle.IsInvalid)
            ClearNativeDisconnectCallback(handle.Value);
        handle.Dispose();
    }

    private void EnqueueInboundConnection(int handle)
    {
        if (_inboundSignals?.Writer.TryWrite(new(handle)) == true) return;
        _native.Disconnect(handle);
    }

    private void EnqueueInboundDisconnect() => _inboundSignals?.Writer.TryWrite(new(null));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnInboundConnected(int handle)
    {
        var listener = Volatile.Read(ref s_listener);
        if (listener is null) NativeMethods.Disconnect(handle);
        else listener.EnqueueInboundConnection(handle);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnInboundDisconnected() => Volatile.Read(ref s_listener)?.EnqueueInboundDisconnect();

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void OnNativeRead(NativeAutoReadOutput output)
    {
        var active = Volatile.Read(ref s_active);
        if (active is null) return;
        try
        {
            var epc = ReadCString(output.Epc, 513);
            var readerId = ReadCString(output.Id, 25);
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

    private unsafe int StartInboundListener(int port) => _native.ListenTcpClientMode(port, &OnInboundConnected);
    private unsafe int StartNativeListening(int handle) => _native.StartListenAutoRead(handle, &OnNativeRead);
    private unsafe void SetNativeDisconnectCallback(int handle) =>
        _native.SetDisconnectCallback(handle, &OnInboundDisconnected);
    private unsafe void ClearNativeDisconnectCallback(int handle) => _native.SetDisconnectCallback(handle, null);

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

    private static TaskCompletionSource NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly record struct InboundSignal(int? ConnectedHandle);
}
