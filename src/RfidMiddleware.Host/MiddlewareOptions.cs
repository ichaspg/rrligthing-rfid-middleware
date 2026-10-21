using RfidMiddleware.Reader;
using RfidMiddleware.Reader.Vendor;
using RfidMiddleware.Server;

namespace RfidMiddleware.Host;

public sealed class MiddlewareOptions
{
    public string Id { get; set; } = "WH-A-GATE-01";
    public string DatabaseConnectionString { get; set; } = string.Empty;
    public string ControlSocket { get; set; } = "data/control.sock";
    public string ReaderLockFile { get; set; } = "data/reader.lock";
    public string CommissioningBackupDirectory { get; set; } = "data/commissioning";
    public int ManifestPollSeconds { get; set; } = 5;
    public int ManifestWarningSeconds { get; set; } = 300;
    public int DedupWindowMilliseconds { get; set; } = 2000;
    public ManifestMode ManifestMode { get; set; } = ManifestMode.Pull;
    public string ReaderMode { get; set; } = "ManagedFake";
    public FakeReaderOptions FakeReader { get; set; } = new();
    public VendorReaderOptions VendorReader { get; set; } = new();
    public ServerOptions Server { get; set; } = new();
    public PublicApiOptions PublicApi { get; set; } = new();
}

public enum ManifestMode { Pull, Push }

public sealed class PublicApiOptions
{
    public string BindAddress { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 8080;
    public string TokenEnvironmentVariable { get; set; } = "RFID_API_TOKEN";
}
