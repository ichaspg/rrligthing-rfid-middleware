using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RfidMiddleware.Reader.Vendor;

internal static unsafe partial class NativeMethods
{
    internal const string LibraryName = "rfid";
    private static readonly object ResolverLock = new();
    private static bool _resolverInstalled;

    internal static void InstallResolver()
    {
        lock (ResolverLock)
        {
            if (_resolverInstalled) return;
            NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, Resolve);
            _resolverInstalled = true;
        }
    }

    private static nint Resolve(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibraryName) return nint.Zero;
        var configured = Environment.GetEnvironmentVariable("RFID_NATIVE_DIR");
        var directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "native")
            : configured;
        return NativeLibrary.Load(Path.Combine(directory, "librfid.so"));
    }

    [LibraryImport(LibraryName, EntryPoint = "connect_net", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ConnectNet(string ip, int port, NativeTransport protocol, int timeout, out int handle);

    [LibraryImport(LibraryName, EntryPoint = "connect_rs_int", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ConnectSerial(string device, int baud, int address, int timeout, out int handle);

    [LibraryImport(LibraryName, EntryPoint = "connect_mock")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ConnectMock(int port, out int handle);

    [LibraryImport(LibraryName, EntryPoint = "disconnect")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Disconnect(int handle);

    [LibraryImport(LibraryName, EntryPoint = "set_work_mode")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int SetWorkMode(int handle, NativeWorkMode mode);

    [LibraryImport(LibraryName, EntryPoint = "start_listen_auto_read")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int StartListenAutoRead(int handle, delegate* unmanaged[Cdecl]<NativeAutoReadOutput, void> callback);

    [LibraryImport(LibraryName, EntryPoint = "stop_listen_auto_read")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int StopListenAutoRead(int handle);

    [LibraryImport(LibraryName, EntryPoint = "get_ant_count")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int GetAntennaCount(int handle, out int count);

    [LibraryImport(LibraryName, EntryPoint = "set_power")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int SetPower(int handle, int antenna, int power);

    [LibraryImport(LibraryName, EntryPoint = "get_power")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int GetPower(int handle, int antenna, out int power);

    [LibraryImport(LibraryName, EntryPoint = "set_relay_status")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int SetRelayStatus(int handle, int output, NativeRelayStatus status);

    [LibraryImport(LibraryName, EntryPoint = "set_buzz")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int SetBuzz(int handle, NativeBuzzerStatus status);

    [LibraryImport(LibraryName, EntryPoint = "get_network_param")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int GetNetworkParam(int handle, out NativeNetworkParam parameter);

    [LibraryImport(LibraryName, EntryPoint = "free_network_param")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void FreeNetworkParam(ref NativeNetworkParam parameter);

    [LibraryImport(LibraryName, EntryPoint = "set_network_param_with_dns", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int SetNetworkParamWithDns(
        int handle, string ip, string mask, string gateway, int port, nint dns);

    [LibraryImport(LibraryName, EntryPoint = "reboot")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Reboot(int handle);
}

internal enum NativeTransport { Tcp, Udp }
internal enum NativeWorkMode { Command, Auto, Trigger, BidirectionalTrigger }
internal enum NativeRelayStatus { Closed, Open }
internal enum NativeBuzzerStatus { On, Off }

[StructLayout(LayoutKind.Sequential)]
internal struct NativeNetworkParam
{
    internal nint Ip;
    internal nint Mask;
    internal nint Gateway;
    internal int Port;
    internal nint Dns;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeAutoReadOutput
{
    internal int Ant;
    internal fixed int Fin[16];
    internal int FinCount;
    internal fixed byte Door[4];
    internal fixed byte Ip[16];
    internal fixed byte Uid[17];
    internal fixed byte Epc[513];
    internal fixed byte Tid[513];
    internal fixed byte User[513];
    internal fixed byte Id[25];
    internal int Rssi;
    internal int Timestamp;
    internal int TagType;
    internal fixed byte Custom1[25];
    internal fixed byte Custom2[25];
    internal fixed byte Custom3[25];
    internal fixed byte Custom4[25];
    internal fixed byte Custom5[25];
    internal float Temperature;
    internal int Phase;
    internal int Frequency;
    internal float Latitude;
    internal float Longitude;
}

internal sealed class ReaderSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private ReaderSafeHandle() : base(true) { }

    internal static ReaderSafeHandle Create(int value)
    {
        var result = new ReaderSafeHandle();
        result.SetHandle(value);
        return result;
    }

    internal int Value => checked((int)handle);

    protected override bool ReleaseHandle()
    {
        NativeMethods.Disconnect(Value);
        return true;
    }
}
