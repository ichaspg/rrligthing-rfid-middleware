using Microsoft.Extensions.Logging.Abstractions;
using RfidMiddleware.Core;
using RfidMiddleware.Reader.Vendor;

var iterations = args.Length > 0 ? int.Parse(args[0]) : 100;
Console.WriteLine("ABI " + string.Join(", ", NativeAbi.DescribeAutoReadOutput().Select(x => $"{x.Key}={x.Value}")));
var before = Environment.WorkingSet;
for (var i = 0; i < iterations; i++)
{
    await using var reader = new VendorReaderAdapter(NullLogger<VendorReaderAdapter>.Instance, new SystemClock(),
        new VendorReaderOptions { Transport = VendorTransport.NativeMock, MockPort = 26790 + i % 10 });
    await reader.ConnectAsync();
    await reader.DisconnectAsync();
}
GC.Collect();
GC.WaitForPendingFinalizers();
Console.WriteLine($"Completed {iterations} native connect/disconnect cycles. WorkingSetDelta={Environment.WorkingSet - before}");
Console.WriteLine("Network ABI " + string.Join(", ", NativeAbi.DescribeNetworkParam().Select(x => $"{x.Key}={x.Value}")));

NativeMethods.InstallResolver();
NativeReaderNetworkSessionFactory.ThrowIfError(NativeMethods.ConnectMock(26890, out var commissioningHandle),
    "connect commissioning mock");
using (var session = new NativeReaderNetworkSession(
    ReaderSafeHandle.Create(commissioningHandle), ReaderDeviceLease.Acquire(null)))
{
    var beforeNetwork = session.Read();
    var expected = new ReaderNetworkSettings("192.168.50.223", "255.255.255.0", "192.168.50.1", 1969, "192.168.50.2");
    session.Configure(expected);
    var afterNetwork = session.Read();
    if (afterNetwork != expected)
        throw new InvalidOperationException($"Native mock network read-back mismatch: before={beforeNetwork}, after={afterNetwork}");
    NativeReaderNetworkSessionFactory.ThrowIfError(
        NativeMethods.SetBuzz(commissioningHandle, NativeBuzzerStatus.On), "reader buzzer on");
    NativeReaderNetworkSessionFactory.ThrowIfError(
        NativeMethods.SetBuzz(commissioningHandle, NativeBuzzerStatus.Off), "reader buzzer off");
    NativeReaderNetworkSessionFactory.ThrowIfError(
        NativeMethods.SetRelayStatus(commissioningHandle, 1, NativeRelayStatus.Open),
        "digital output 1 on");
    NativeReaderNetworkSessionFactory.ThrowIfError(
        NativeMethods.SetRelayStatus(commissioningHandle, 1, NativeRelayStatus.Closed),
        "digital output 1 off");
    session.Reboot();
    Console.WriteLine($"Network commissioning, buzzer, and output 1 mock passed: {afterNetwork.Ip}:{afterNetwork.Port}");
}

