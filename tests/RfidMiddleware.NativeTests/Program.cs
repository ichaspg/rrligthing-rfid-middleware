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

