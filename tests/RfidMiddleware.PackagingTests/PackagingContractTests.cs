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
        Assert.StartsWith("/dev/serial/by-id/", json.RootElement.GetProperty("VendorReader").GetProperty("SerialDevice").GetString(), StringComparison.Ordinal);
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

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RfidMiddleware.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
