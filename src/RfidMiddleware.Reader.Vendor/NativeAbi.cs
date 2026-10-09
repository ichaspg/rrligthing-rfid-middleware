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

    private static int Offset(string field) => checked((int)Marshal.OffsetOf<NativeAutoReadOutput>(field));
}

