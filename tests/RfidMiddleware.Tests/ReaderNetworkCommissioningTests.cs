using RfidMiddleware.Reader.Vendor;

namespace RfidMiddleware.Tests;

public sealed class ReaderNetworkCommissioningTests
{
    private static readonly ReaderNetworkSettings Valid =
        new("192.168.10.20", "255.255.255.0", "192.168.10.1", 1969, "192.168.10.2");

    [Fact]
    public void ValidatesAndNormalizesNetworkSettings()
    {
        var actual = ReaderNetworkValidator.Validate(
            new("192.168.10.20", "255.255.255.0", "192.168.10.1", 1969, "8.8.8.8"));

        Assert.Equal("192.168.10.20", actual.Ip);
        Assert.Equal("8.8.8.8", actual.Dns);
    }

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public void RejectsInvalidNetworkSettings(ReaderNetworkSettings settings) =>
        Assert.Throws<ArgumentException>(() => ReaderNetworkValidator.Validate(settings));

    [Fact]
    public void PreviewReadsWithoutWritingBackingUpOrRebooting()
    {
        var factory = new FakeSessionFactory(Valid);
        var commissioner = new ReaderNetworkCommissioner(factory, () => DateTimeOffset.UnixEpoch);
        var proposed = Valid with { Ip = "192.168.10.21" };

        var result = commissioner.ConfigureSerial(SerialOptions(), "", proposed, false, "unused");

        Assert.False(result.Applied);
        Assert.False(result.Rebooted);
        Assert.Null(result.BackupFile);
        Assert.Equal(0, factory.Session.ConfigureCalls);
        Assert.Equal(0, factory.Session.RebootCalls);
    }

    [Fact]
    public void ApplyBacksUpWritesVerifiesAndReboots()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rfid-commission-{Guid.NewGuid():N}");
        try
        {
            var factory = new FakeSessionFactory(Valid);
            var commissioner = new ReaderNetworkCommissioner(factory,
                () => DateTimeOffset.Parse("2026-10-19T00:00:00Z"));
            var proposed = Valid with { Ip = "192.168.10.21" };

            var result = commissioner.ConfigureSerial(SerialOptions(), "", proposed, true, directory);

            Assert.True(result.Applied);
            Assert.True(result.Rebooted);
            Assert.Equal(proposed, result.ReadBack);
            Assert.Equal(1, factory.Session.ConfigureCalls);
            Assert.Equal(1, factory.Session.RebootCalls);
            Assert.NotNull(result.BackupFile);
            Assert.True(File.Exists(result.BackupFile));
            Assert.Contains("192.168.10.20", File.ReadAllText(result.BackupFile), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReadBackMismatchDoesNotReboot()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rfid-commission-{Guid.NewGuid():N}");
        try
        {
            var factory = new FakeSessionFactory(Valid) { PersistConfiguration = false };
            var commissioner = new ReaderNetworkCommissioner(factory, () => DateTimeOffset.UnixEpoch);

            Assert.Throws<ReaderNetworkVerificationException>(() => commissioner.ConfigureSerial(
                SerialOptions(), "", Valid with { Ip = "192.168.10.21" }, true, directory));
            Assert.Equal(1, factory.Session.ConfigureCalls);
            Assert.Equal(0, factory.Session.RebootCalls);
            Assert.Single(Directory.GetFiles(directory, "*.json"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RebootFailureIsReturnedAfterVerifiedWrite()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rfid-commission-{Guid.NewGuid():N}");
        try
        {
            var factory = new FakeSessionFactory(Valid);
            factory.Session.RebootException = new InvalidOperationException("reboot failed");
            var commissioner = new ReaderNetworkCommissioner(factory, () => DateTimeOffset.UnixEpoch);

            var error = Assert.Throws<InvalidOperationException>(() => commissioner.ConfigureSerial(
                SerialOptions(), "", Valid with { Ip = "192.168.10.21" }, true, directory));
            Assert.Equal("reboot failed", error.Message);
            Assert.Equal(1, factory.Session.ConfigureCalls);
            Assert.Equal(1, factory.Session.RebootCalls);
            Assert.Single(Directory.GetFiles(directory, "*.json"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void VerifyTcpRejectsUnexpectedReaderAddress()
    {
        var factory = new FakeSessionFactory(Valid);
        var commissioner = new ReaderNetworkCommissioner(factory, () => DateTimeOffset.UnixEpoch);

        Assert.Throws<ReaderNetworkVerificationException>(() =>
            commissioner.VerifyTcp("192.168.10.21", 1969, 6000, ""));
    }

    [Fact]
    public void InaccessibleSerialDeviceIsReported()
    {
        var factory = new FakeSessionFactory(Valid) { OpenException = new IOException("serial unavailable") };
        var commissioner = new ReaderNetworkCommissioner(factory, () => DateTimeOffset.UnixEpoch);

        var error = Assert.Throws<IOException>(() => commissioner.ShowSerial(SerialOptions(), ""));
        Assert.Equal("serial unavailable", error.Message);
    }

    [Fact]
    public void RequiresBafoRs232SerialSettings()
    {
        var factory = new FakeSessionFactory(Valid);
        var commissioner = new ReaderNetworkCommissioner(factory, () => DateTimeOffset.UnixEpoch);
        var options = SerialOptions();
        options.Rs485Address = 33;

        Assert.Throws<ArgumentException>(() => commissioner.ShowSerial(options, ""));
    }

    [Fact]
    public void ReaderLockRejectsConcurrentOwnership()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rfid-lock-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "reader.lock");
        try
        {
            using var first = ReaderDeviceLease.Acquire(path);
            Assert.Throws<ReaderDeviceBusyException>(() => ReaderDeviceLease.Acquire(path));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void NetworkNativeLayoutMatchesX64Header()
    {
        var abi = NativeAbi.DescribeNetworkParam();
        Assert.Equal(40, abi["size"]);
        Assert.Equal(0, abi["ip"]);
        Assert.Equal(8, abi["mask"]);
        Assert.Equal(16, abi["gateway"]);
        Assert.Equal(24, abi["port"]);
        Assert.Equal(32, abi["dns"]);
    }

    public static TheoryData<ReaderNetworkSettings> InvalidSettings() => new()
    {
        new("not-an-ip", "255.255.255.0", "192.168.10.1", 1969, null),
        new("192.168.010.020", "255.255.255.0", "192.168.10.1", 1969, null),
        new("192.168.10.20", "255.0.255.0", "192.168.10.1", 1969, null),
        new("192.168.10.0", "255.255.255.0", "192.168.10.1", 1969, null),
        new("192.168.10.255", "255.255.255.0", "192.168.10.1", 1969, null),
        new("192.168.10.20", "255.255.255.0", "192.168.11.1", 1969, null),
        new("192.168.10.20", "255.255.255.0", "192.168.10.1", 0, null),
        new("192.168.10.20", "255.255.255.0", "192.168.10.1", 1969, "::1")
    };

    private static VendorReaderOptions SerialOptions() => new()
    {
        Transport = VendorTransport.Serial,
        SerialDevice = "/dev/rfid-reader",
        Baud = 115200,
        Rs485Address = 0
    };

    private sealed class FakeSessionFactory(ReaderNetworkSettings initial) : IReaderNetworkSessionFactory
    {
        internal FakeSession Session { get; } = new(initial);
        internal bool PersistConfiguration { get => Session.PersistConfiguration; set => Session.PersistConfiguration = value; }
        internal Exception? OpenException { get; set; }

        public IReaderNetworkSession OpenSerial(VendorReaderOptions options, string lockFile) =>
            OpenException is null ? Session : throw OpenException;
        public IReaderNetworkSession OpenTcp(string address, int port, int timeoutMilliseconds, string lockFile) =>
            OpenException is null ? Session : throw OpenException;
    }

    private sealed class FakeSession(ReaderNetworkSettings settings) : IReaderNetworkSession
    {
        private ReaderNetworkSettings _settings = settings;
        internal int ConfigureCalls { get; private set; }
        internal int RebootCalls { get; private set; }
        internal bool PersistConfiguration { get; set; } = true;
        internal Exception? RebootException { get; set; }

        public ReaderNetworkSettings Read() => _settings;
        public void Configure(ReaderNetworkSettings value)
        {
            ConfigureCalls++;
            if (PersistConfiguration) _settings = value;
        }
        public void Reboot()
        {
            RebootCalls++;
            if (RebootException is not null) throw RebootException;
        }
        public void Dispose() { }
    }
}
