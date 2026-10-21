using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using RfidMiddleware.Core;
using RfidMiddleware.Reader.Vendor;

namespace RfidMiddleware.Tests;

public sealed class VendorReaderTcpListenerTests
{
    [Fact]
    public async Task WaitsAcceptsOneReconnectsAndShutsDownCleanly()
    {
        var native = new FakeNativeApi();
        var options = new VendorReaderOptions
        {
            Transport = VendorTransport.TcpListener,
            Port = 2970,
            AlarmPulseMilliseconds = 100
        };
        await using var adapter = new VendorReaderAdapter(
            NullLogger<VendorReaderAdapter>.Instance, new SystemClock(), options, native);

        var connecting = adapter.ConnectAsync().AsTask();
        Assert.False(connecting.IsCompleted);
        Assert.Contains("Waiting", adapter.Health.Error, StringComparison.Ordinal);

        native.RaiseConnected(101);
        await connecting.WaitAsync(TimeSpan.FromSeconds(2));
        await adapter.StartInventoryAsync(_ => true);
        Assert.True(adapter.Health.Connected);
        Assert.True(adapter.Health.InventoryRunning);
        Assert.Equal(1, native.StartInventoryCalls);

        native.RaiseConnected(202);
        await WaitUntilAsync(() => native.DisconnectedHandles.Contains(202));
        Assert.Equal(101, adapter.Handle);

        var alarm = new VendorAlarmController(adapter, new SystemClock(), options);
        await alarm.SignalUnauthorizedAsync();
        Assert.True(native.BuzzCalls > 0);
        Assert.True(native.RelayCalls > 0);
        await alarm.DisposeAsync();

        native.RaiseDisconnected();
        await WaitUntilAsync(() => !adapter.Health.Connected);
        Assert.Contains("Waiting", adapter.Health.Error, StringComparison.Ordinal);

        native.RaiseConnected(303);
        await WaitUntilAsync(() => adapter.Health.Connected && adapter.Health.InventoryRunning);
        Assert.Equal(303, adapter.Handle);
        Assert.Equal(2, native.StartInventoryCalls);

        await adapter.DisconnectAsync();
        Assert.Equal(1, native.StopListenerCalls);
        Assert.False(adapter.Health.Connected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void RejectsInvalidListenerPort(int port)
    {
        var options = new VendorReaderOptions { Transport = VendorTransport.TcpListener, Port = port };
        Assert.Throws<ArgumentException>(() => VendorReaderOptionsValidator.Validate(options));
    }

    [Fact]
    public async Task ListenerStartupFailureReleasesSingleListenerOwnership()
    {
        var options = new VendorReaderOptions { Transport = VendorTransport.TcpListener, Port = 2971 };
        var failingNative = new FakeNativeApi { ListenResult = -77 };
        await using (var failing = new VendorReaderAdapter(
            NullLogger<VendorReaderAdapter>.Instance, new SystemClock(), options, failingNative))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => failing.ConnectAsync().AsTask());
            Assert.Contains("-77", error.Message, StringComparison.Ordinal);
        }

        var healthyNative = new FakeNativeApi();
        await using var healthy = new VendorReaderAdapter(
            NullLogger<VendorReaderAdapter>.Instance, new SystemClock(), options, healthyNative);
        var connecting = healthy.ConnectAsync().AsTask();
        healthyNative.RaiseConnected(404);
        await connecting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(healthy.Health.Connected);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Condition was not reached.");
            await Task.Delay(10);
        }
    }

    private sealed unsafe class FakeNativeApi : IVendorNativeApi
    {
        private delegate* unmanaged[Cdecl]<int, void> _connected;
        private delegate* unmanaged[Cdecl]<void> _disconnected;
        internal List<int> DisconnectedHandles { get; } = [];
        internal int StartInventoryCalls { get; private set; }
        internal int StopListenerCalls { get; private set; }
        internal int BuzzCalls { get; private set; }
        internal int RelayCalls { get; private set; }
        internal int ListenResult { get; init; }

        public int ConnectMock(int port, out int handle) { handle = 0; return -1; }
        public int ConnectNet(string address, int port, NativeTransport protocol, int timeoutMilliseconds,
            out int handle) { handle = 0; return -1; }
        public int ConnectSerial(string device, int baud, int address, int timeoutMilliseconds,
            out int handle) { handle = 0; return -1; }
        public void Disconnect(int handle) => DisconnectedHandles.Add(handle);
        public int SetWorkMode(int handle, NativeWorkMode mode) => 0;
        public int StartListenAutoRead(int handle, delegate* unmanaged[Cdecl]<NativeAutoReadOutput, void> callback)
        {
            StartInventoryCalls++;
            return 0;
        }
        public int StopListenAutoRead(int handle) => 0;
        public int ListenTcpClientMode(int port, delegate* unmanaged[Cdecl]<int, void> callback)
        {
            _connected = callback;
            return ListenResult;
        }
        public void StopListenTcpClientMode(int port) => StopListenerCalls++;
        public void SetDisconnectCallback(int handle, delegate* unmanaged[Cdecl]<void> callback) =>
            _disconnected = callback;
        public int GetAntennaCount(int handle, out int count) { count = 1; return 0; }
        public int SetPower(int handle, int antenna, int power) => 0;
        public int GetPower(int handle, int antenna, out int power) { power = 10; return 0; }
        public int SetRelayStatus(int handle, int output, NativeRelayStatus status) { RelayCalls++; return 0; }
        public int SetBuzz(int handle, NativeBuzzerStatus status) { BuzzCalls++; return 0; }

        internal void RaiseConnected(int handle)
        {
            if (_connected == null) throw new InvalidOperationException("Listener is not active.");
            _connected(handle);
        }

        internal void RaiseDisconnected()
        {
            if (_disconnected == null) throw new InvalidOperationException("Disconnect callback is not active.");
            _disconnected();
        }
    }
}
