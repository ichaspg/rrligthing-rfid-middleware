using RfidMiddleware.Reader.Vendor;

namespace RfidMiddleware.Tests;

public sealed class ReaderClientCommissioningTests
{
    private static readonly ReaderClientReportSettings Initial = new(
        new ReaderClientTargetSettings("192.168.10.50", 3000, true),
        new ReaderClientTargetSettings("198.51.100.10", 1969, false));

    [Theory]
    [InlineData("not-an-ip", 1969)]
    [InlineData("192.168.010.020", 1969)]
    [InlineData("198.51.100.10", 0)]
    [InlineData("198.51.100.10", 65536)]
    public void RejectsInvalidEnabledTarget(string address, int port) =>
        Assert.Throws<ArgumentException>(() => ReaderClientTargetValidator.ValidateEnabled(
            new ReaderClientTargetSettings(address, port, true)));

    [Fact]
    public void PreviewPreservesUdpAndDoesNotWrite()
    {
        var factory = new FakeClientSessionFactory(Initial);
        var commissioner = new ReaderClientCommissioner(factory, () => DateTimeOffset.UnixEpoch);

        var result = commissioner.ConfigureSerial(SerialOptions(), "",
            new ReaderClientTargetSettings("203.0.113.20", 1969, true), false, "unused");

        Assert.Equal(Initial.Udp, result.Proposed.Udp);
        Assert.Equal("203.0.113.20", result.Proposed.Tcp.ServerIp);
        Assert.False(result.Applied);
        Assert.Equal(0, factory.Session.ConfigureCalls);
        Assert.Equal(0, factory.Session.RebootCalls);
    }

    [Fact]
    public void ApplyBacksUpWritesVerifiesAndRebootsWithoutChangingUdp()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rfid-client-{Guid.NewGuid():N}");
        try
        {
            var factory = new FakeClientSessionFactory(Initial);
            var commissioner = new ReaderClientCommissioner(factory,
                () => DateTimeOffset.Parse("2026-10-21T00:00:00Z"));

            var result = commissioner.ConfigureSerial(SerialOptions(), "",
                new ReaderClientTargetSettings("203.0.113.20", 2970, true), true, directory);

            Assert.True(result.Applied);
            Assert.True(result.Rebooted);
            Assert.Equal(Initial.Udp, result.ReadBack!.Udp);
            Assert.Equal(new ReaderClientTargetSettings("203.0.113.20", 2970, true), result.ReadBack.Tcp);
            Assert.Equal(1, factory.Session.ConfigureCalls);
            Assert.Equal(1, factory.Session.RebootCalls);
            Assert.Contains("198.51.100.10", File.ReadAllText(result.BackupFile!), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DisablePreservesStoredTargetAndTurnsOffTcpReporting()
    {
        var factory = new FakeClientSessionFactory(Initial with
        {
            Tcp = Initial.Tcp with { Enabled = true }
        });
        var commissioner = new ReaderClientCommissioner(factory, () => DateTimeOffset.UnixEpoch);

        var result = commissioner.DisableSerial(SerialOptions(), "", false, "unused");

        Assert.Equal(Initial.Tcp.ServerIp, result.Proposed.Tcp.ServerIp);
        Assert.Equal(Initial.Tcp.Port, result.Proposed.Tcp.Port);
        Assert.False(result.Proposed.Tcp.Enabled);
    }

    [Fact]
    public void ReadBackMismatchDoesNotReboot()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rfid-client-{Guid.NewGuid():N}");
        try
        {
            var factory = new FakeClientSessionFactory(Initial) { PersistConfiguration = false };
            var commissioner = new ReaderClientCommissioner(factory, () => DateTimeOffset.UnixEpoch);

            Assert.Throws<ReaderNetworkVerificationException>(() => commissioner.ConfigureSerial(
                SerialOptions(), "", new ReaderClientTargetSettings("203.0.113.20", 1969, true), true, directory));
            Assert.Equal(0, factory.Session.RebootCalls);
            Assert.Single(Directory.GetFiles(directory, "*.json"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReportOptionNativeLayoutMatchesHeader()
    {
        var abi = NativeAbi.DescribeReportOption();
        Assert.Equal(72, abi["size"]);
        Assert.Equal(0, abi["ip"]);
        Assert.Equal(64, abi["port"]);
        Assert.Equal(68, abi["enable"]);
    }

    private static VendorReaderOptions SerialOptions() => new()
    {
        Transport = VendorTransport.Serial,
        SerialDevice = "/dev/rfid-reader",
        Baud = 115200,
        Rs485Address = 0
    };

    private sealed class FakeClientSessionFactory(ReaderClientReportSettings initial) : IReaderClientSessionFactory
    {
        internal FakeClientSession Session { get; } = new(initial);
        internal bool PersistConfiguration
        {
            get => Session.PersistConfiguration;
            set => Session.PersistConfiguration = value;
        }

        public IReaderClientSession OpenSerial(VendorReaderOptions options, string lockFile) => Session;
    }

    private sealed class FakeClientSession(ReaderClientReportSettings settings) : IReaderClientSession
    {
        private ReaderClientReportSettings _settings = settings;
        internal int ConfigureCalls { get; private set; }
        internal int RebootCalls { get; private set; }
        internal bool PersistConfiguration { get; set; } = true;

        public ReaderClientReportSettings Read() => _settings;
        public void Configure(ReaderClientReportSettings value)
        {
            ConfigureCalls++;
            if (PersistConfiguration) _settings = value;
        }
        public void Reboot() => RebootCalls++;
        public void Dispose() { }
    }
}
