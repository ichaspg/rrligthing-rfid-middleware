using System.Runtime.CompilerServices;

namespace RfidMiddleware.Reader.Vendor;

internal unsafe interface IVendorNativeApi
{
    int ConnectMock(int port, out int handle);
    int ConnectNet(string address, int port, NativeTransport protocol, int timeoutMilliseconds, out int handle);
    int ConnectSerial(string device, int baud, int address, int timeoutMilliseconds, out int handle);
    void Disconnect(int handle);
    int SetWorkMode(int handle, NativeWorkMode mode);
    int StartListenAutoRead(int handle, delegate* unmanaged[Cdecl]<NativeAutoReadOutput, void> callback);
    int StopListenAutoRead(int handle);
    int ListenTcpClientMode(int port, delegate* unmanaged[Cdecl]<int, void> callback);
    void StopListenTcpClientMode(int port);
    void SetDisconnectCallback(int handle, delegate* unmanaged[Cdecl]<void> callback);
    int GetAntennaCount(int handle, out int count);
    int SetPower(int handle, int antenna, int power);
    int GetPower(int handle, int antenna, out int power);
    int SetRelayStatus(int handle, int output, NativeRelayStatus status);
    int SetBuzz(int handle, NativeBuzzerStatus status);
}

internal sealed unsafe class PInvokeVendorNativeApi : IVendorNativeApi
{
    internal PInvokeVendorNativeApi() => NativeMethods.InstallResolver();

    public int ConnectMock(int port, out int handle) => NativeMethods.ConnectMock(port, out handle);
    public int ConnectNet(string address, int port, NativeTransport protocol, int timeoutMilliseconds, out int handle) =>
        NativeMethods.ConnectNet(address, port, protocol, timeoutMilliseconds, out handle);
    public int ConnectSerial(string device, int baud, int address, int timeoutMilliseconds, out int handle) =>
        NativeMethods.ConnectSerial(device, baud, address, timeoutMilliseconds, out handle);
    public void Disconnect(int handle) => NativeMethods.Disconnect(handle);
    public int SetWorkMode(int handle, NativeWorkMode mode) => NativeMethods.SetWorkMode(handle, mode);
    public int StartListenAutoRead(int handle, delegate* unmanaged[Cdecl]<NativeAutoReadOutput, void> callback) =>
        NativeMethods.StartListenAutoRead(handle, callback);
    public int StopListenAutoRead(int handle) => NativeMethods.StopListenAutoRead(handle);
    public int ListenTcpClientMode(int port, delegate* unmanaged[Cdecl]<int, void> callback) =>
        NativeMethods.ListenTcpClientMode(port, callback);
    public void StopListenTcpClientMode(int port) => NativeMethods.StopListenTcpClientMode(port);
    public void SetDisconnectCallback(int handle, delegate* unmanaged[Cdecl]<void> callback) =>
        NativeMethods.SetDisconnectCallback(handle, callback);
    public int GetAntennaCount(int handle, out int count) => NativeMethods.GetAntennaCount(handle, out count);
    public int SetPower(int handle, int antenna, int power) => NativeMethods.SetPower(handle, antenna, power);
    public int GetPower(int handle, int antenna, out int power) => NativeMethods.GetPower(handle, antenna, out power);
    public int SetRelayStatus(int handle, int output, NativeRelayStatus status) =>
        NativeMethods.SetRelayStatus(handle, output, status);
    public int SetBuzz(int handle, NativeBuzzerStatus status) => NativeMethods.SetBuzz(handle, status);
}
