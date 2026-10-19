using System.Runtime.InteropServices;

namespace RfidMiddleware.Reader.Vendor;

public static class NativeAbi
{
    public static IReadOnlyDictionary<string, int> DescribeAutoReadOutput() => new Dictionary<string, int>
    {
        ["size"] = Marshal.SizeOf<NativeAutoReadOutput>(),
        ["ant"] = Offset(nameof(NativeAutoReadOutput.Ant)),
        ["fin_count"] = Offset(nameof(NativeAutoReadOutput.FinCount)),
        ["rssi"] = Offset(nameof(NativeAutoReadOutput.Rssi)),
        ["timestamp"] = Offset(nameof(NativeAutoReadOutput.Timestamp)),
        ["temperature"] = Offset(nameof(NativeAutoReadOutput.Temperature)),
        ["longitude"] = Offset(nameof(NativeAutoReadOutput.Longitude))
    };

    public static IReadOnlyDictionary<string, int> DescribeNetworkParam() => new Dictionary<string, int>
    {
        ["size"] = Marshal.SizeOf<NativeNetworkParam>(),
        ["ip"] = NetworkOffset(nameof(NativeNetworkParam.Ip)),
        ["mask"] = NetworkOffset(nameof(NativeNetworkParam.Mask)),
        ["gateway"] = NetworkOffset(nameof(NativeNetworkParam.Gateway)),
        ["port"] = NetworkOffset(nameof(NativeNetworkParam.Port)),
        ["dns"] = NetworkOffset(nameof(NativeNetworkParam.Dns))
    };

    private static int Offset(string field) => checked((int)Marshal.OffsetOf<NativeAutoReadOutput>(field));
    private static int NetworkOffset(string field) => checked((int)Marshal.OffsetOf<NativeNetworkParam>(field));
}

