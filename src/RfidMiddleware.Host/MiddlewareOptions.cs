using RfidMiddleware.Reader;
using RfidMiddleware.Reader.Vendor;
using RfidMiddleware.Server;

namespace RfidMiddleware.Host;

public sealed class MiddlewareOptions
{
    public string Id { get; set; } = "WH-A-GATE-01";
    public string DatabaseConnectionString { get; set; } = string.Empty;
    public string ControlSocket { get; set; } = "data/control.sock";
    public int ManifestPollSeconds { get; set; } = 5;
    public int ManifestWarningSeconds { get; set; } = 300;
    public int DedupWindowMilliseconds { get; set; } = 2000;
    public string ReaderMode { get; set; } = "ManagedFake";
    public FakeReaderOptions FakeReader { get; set; } = new();
    public VendorReaderOptions VendorReader { get; set; } = new();
    public ServerOptions Server { get; set; } = new();
}
