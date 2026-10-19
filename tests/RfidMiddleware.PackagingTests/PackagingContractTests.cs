using System.Text.Json;
using Xunit;

namespace RfidMiddleware.PackagingTests;

public sealed class PackagingContractTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void SystemdUnitUsesRestrictedServiceAccountAndWritablePaths()
    {
        var unit = File.ReadAllText(Path.Combine(Root, "packaging", "rfid-middleware.service"));
        Assert.Contains("User=rfid-middleware", unit, StringComparison.Ordinal);
        Assert.Contains("SupplementaryGroups=dialout", unit, StringComparison.Ordinal);
        Assert.Contains("NoNewPrivileges=true", unit, StringComparison.Ordinal);
        Assert.Contains("ProtectSystem=strict", unit, StringComparison.Ordinal);
        Assert.Contains("ReadWritePaths=/var/lib/rfid-middleware /run/rfid-middleware", unit, StringComparison.Ordinal);
        Assert.Contains("Restart=on-failure", unit, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionConfigUsesPostgresAndLinuxSocketLocation()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "packaging", "appsettings.production.json")));
        Assert.Equal(string.Empty, json.RootElement.GetProperty("DatabaseConnectionString").GetString());
        Assert.Equal("/run/rfid-middleware/control.sock", json.RootElement.GetProperty("ControlSocket").GetString());
        Assert.Equal("/run/rfid-middleware/reader.lock", json.RootElement.GetProperty("ReaderLockFile").GetString());
        Assert.Equal("/var/lib/rfid-middleware/commissioning",
            json.RootElement.GetProperty("CommissioningBackupDirectory").GetString());
        Assert.StartsWith("/dev/serial/by-id/", json.RootElement.GetProperty("VendorReader").GetProperty("SerialDevice").GetString(), StringComparison.Ordinal);
        Assert.Equal("BuzzerAndDigitalOutputs",
            json.RootElement.GetProperty("VendorReader").GetProperty("AlarmMode").GetString());
        Assert.False(json.RootElement.GetProperty("VendorReader").TryGetProperty("RelayNumber", out _));
    }

    [Fact]
    public void HostPublishIncludesBothNativeLibraries()
    {
        var project = File.ReadAllText(Path.Combine(Root, "src", "RfidMiddleware.Host", "RfidMiddleware.Host.csproj"));
        Assert.Contains("native/librfid.so", project, StringComparison.Ordinal);
        Assert.Contains("native/librfid_core.so", project, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageBuildRepairsVendorRunpath()
    {
        var script = File.ReadAllText(Path.Combine(Root, "scripts", "build-deb.sh"));
        Assert.Contains("patchelf --set-rpath '$ORIGIN'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DockerImageIsNonRootAndChecksNativeDependencies()
    {
        var dockerfile = File.ReadAllText(Path.Combine(Root, "Dockerfile"));
        Assert.Contains("AS middleware", dockerfile, StringComparison.Ordinal);
        Assert.Contains("AS mock-backend", dockerfile, StringComparison.Ordinal);
        Assert.Contains("aspnet:8.0-noble", dockerfile, StringComparison.Ordinal);
        Assert.Contains("patchelf --set-rpath '$ORIGIN'", dockerfile, StringComparison.Ordinal);
        Assert.Contains("ldd /out/middleware/native/librfid.so", dockerfile, StringComparison.Ordinal);
        Assert.Contains("USER rfid-middleware:rfid-middleware", dockerfile, StringComparison.Ordinal);
        Assert.Contains("\"healthcheck\"", dockerfile, StringComparison.Ordinal);
    }

    [Fact]
    public void ShippingComposeExcludesMockBackendAndDevelopmentOverrideAddsIt()
    {
        var shipping = File.ReadAllText(Path.Combine(Root, "docker-compose.yml"));
        var development = File.ReadAllText(Path.Combine(Root, "docker-compose.dev.yml"));
        Assert.DoesNotContain("mock-backend", shipping, StringComparison.Ordinal);
        Assert.DoesNotContain("dev-token", shipping, StringComparison.Ordinal);
        Assert.Contains("mock-backend:", development, StringComparison.Ordinal);
        Assert.Contains("target: mock-backend", development, StringComparison.Ordinal);
        Assert.Contains("RFID_API_TOKEN: dev-token", development, StringComparison.Ordinal);
        Assert.Contains("RFID_ReaderMode: ManagedFake", development, StringComparison.Ordinal);
        Assert.Contains("RFID_VendorReader__AlarmMode: ${RFID_READER_ALARM_MODE:-BuzzerAndDigitalOutputs}", shipping,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DockerReaderOverridesDoNotPublishReaderPort()
    {
        var shipping = File.ReadAllText(Path.Combine(Root, "docker-compose.yml"));
        var tcp = File.ReadAllText(Path.Combine(Root, "docker-compose.tcp.yml"));
        var serial = File.ReadAllText(Path.Combine(Root, "docker-compose.serial.yml"));
        Assert.DoesNotContain("1969:1969", shipping, StringComparison.Ordinal);
        Assert.DoesNotContain("ports:", tcp, StringComparison.Ordinal);
        Assert.DoesNotContain("ports:", serial, StringComparison.Ordinal);
        Assert.Contains("/dev/rfid-reader", serial, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RfidMiddleware.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
