using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RfidMiddleware.Reader.Vendor;

public sealed record ReaderNetworkSettings(string Ip, string Mask, string Gateway, int Port, string? Dns);

public sealed record ReaderNetworkCommissioningResult(
    ReaderNetworkSettings Current,
    ReaderNetworkSettings Proposed,
    ReaderNetworkSettings? ReadBack,
    bool Applied,
    bool Rebooted,
    string? BackupFile);

public sealed class ReaderNetworkVerificationException(string message) : InvalidOperationException(message);

public sealed class ReaderNetworkCommissioner
{
    private readonly IReaderNetworkSessionFactory _sessions;
    private readonly Func<DateTimeOffset> _utcNow;

    public ReaderNetworkCommissioner() : this(new NativeReaderNetworkSessionFactory(), () => DateTimeOffset.UtcNow) { }

    internal ReaderNetworkCommissioner(IReaderNetworkSessionFactory sessions, Func<DateTimeOffset> utcNow)
    {
        _sessions = sessions;
        _utcNow = utcNow;
    }

    public ReaderNetworkSettings ShowSerial(VendorReaderOptions options, string lockFile)
    {
        ValidateSerialOptions(options);
        using var session = _sessions.OpenSerial(options, lockFile);
        return session.Read();
    }

    public ReaderNetworkCommissioningResult ConfigureSerial(
        VendorReaderOptions options,
        string lockFile,
        ReaderNetworkSettings proposed,
        bool apply,
        string backupDirectory)
    {
        ValidateSerialOptions(options);
        var normalized = ReaderNetworkValidator.Validate(proposed);
        using var session = _sessions.OpenSerial(options, lockFile);
        var current = session.Read();
        if (!apply)
            return new ReaderNetworkCommissioningResult(current, normalized, null, false, false, null);

        var backupFile = SaveBackup(backupDirectory, options, current);
        session.Configure(normalized);
        var readBack = NormalizeReadBack(session.Read());
        if (readBack != normalized)
            throw new ReaderNetworkVerificationException(
                $"Reader network read-back did not match the requested configuration. Backup: {backupFile}");

        session.Reboot();
        return new ReaderNetworkCommissioningResult(current, normalized, readBack, true, true, backupFile);
    }

    public ReaderNetworkSettings VerifyTcp(string address, int port, int timeoutMilliseconds, string lockFile)
    {
        var expectedAddress = ReaderNetworkValidator.ParseIpv4(address, "address").ToString();
        ReaderNetworkValidator.ValidatePort(port);
        if (timeoutMilliseconds is < 100 or > 120_000)
            throw new ArgumentException("TCP timeout must be between 100 and 120000 milliseconds.");

        using var session = _sessions.OpenTcp(expectedAddress, port, timeoutMilliseconds, lockFile);
        var actual = NormalizeReadBack(session.Read());
        if (!string.Equals(actual.Ip, expectedAddress, StringComparison.Ordinal) || actual.Port != port)
            throw new ReaderNetworkVerificationException(
                $"Connected reader reports {actual.Ip}:{actual.Port}, expected {expectedAddress}:{port}.");
        return actual;
    }

    private string SaveBackup(string directory, VendorReaderOptions options, ReaderNetworkSettings current)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Commissioning backup directory is required.", nameof(directory));

        var fullDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullDirectory);
        var stamp = _utcNow().ToString("yyyyMMddTHHmmssfffZ");
        var finalPath = Path.Combine(fullDirectory, $"reader-network-{stamp}-{Guid.NewGuid():N}.json");
        var temporaryPath = finalPath + ".tmp";
        var payload = JsonSerializer.Serialize(new
        {
            createdAt = _utcNow(),
            connection = new
            {
                transport = "Serial",
                options.SerialDevice,
                options.Baud,
                options.Rs485Address
            },
            settings = current
        }, new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(temporaryPath, payload);
        File.Move(temporaryPath, finalPath);
        return finalPath;
    }

    private static ReaderNetworkSettings NormalizeReadBack(ReaderNetworkSettings settings) => new(
        settings.Ip.Trim(),
        settings.Mask.Trim(),
        settings.Gateway.Trim(),
        settings.Port,
        string.IsNullOrWhiteSpace(settings.Dns) ? null : settings.Dns.Trim());

    private static void ValidateSerialOptions(VendorReaderOptions options)
    {
        if (options.Transport != VendorTransport.Serial)
            throw new ArgumentException("Reader network commissioning requires VendorReader.Transport=Serial.");
        if (string.IsNullOrWhiteSpace(options.SerialDevice))
            throw new ArgumentException("VendorReader.SerialDevice is required.");
        if (options.Baud != 115200)
            throw new ArgumentException("BAFO RS-232 commissioning requires baud 115200.");
        if (options.Rs485Address != 0)
            throw new ArgumentException("BAFO RS-232 commissioning requires reader address 0.");
    }
}

internal static class ReaderNetworkValidator
{
    internal static ReaderNetworkSettings Validate(ReaderNetworkSettings settings)
    {
        var ip = ParseIpv4(settings.Ip, "ip");
        var mask = ParseIpv4(settings.Mask, "mask");
        var gateway = ParseIpv4(settings.Gateway, "gateway");
        var dns = string.IsNullOrWhiteSpace(settings.Dns) ? null : ParseIpv4(settings.Dns, "dns");
        ValidatePort(settings.Port);

        var ipValue = ToUInt32(ip);
        var maskValue = ToUInt32(mask);
        var gatewayValue = ToUInt32(gateway);
        var invertedMask = ~maskValue;
        if (maskValue == 0 || maskValue == uint.MaxValue || (invertedMask & (invertedMask + 1)) != 0)
            throw new ArgumentException("mask must be a contiguous IPv4 subnet mask between /1 and /30.");

        var network = ipValue & maskValue;
        var broadcast = network | invertedMask;
        if (ipValue == network || ipValue == broadcast)
            throw new ArgumentException("ip cannot be the subnet network or broadcast address.");
        if ((gatewayValue & maskValue) != network || gatewayValue == network || gatewayValue == broadcast)
            throw new ArgumentException("gateway must be a usable address in the reader subnet.");

        return new ReaderNetworkSettings(ip.ToString(), mask.ToString(), gateway.ToString(), settings.Port, dns?.ToString());
    }

    internal static IPAddress ParseIpv4(string value, string name)
    {
        var canonicalInput = value.Trim();
        if (!IPAddress.TryParse(canonicalInput, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork ||
            !string.Equals(address.ToString(), canonicalInput, StringComparison.Ordinal))
            throw new ArgumentException($"{name} must be a canonical dotted-decimal IPv4 address.");
        return address;
    }

    internal static void ValidatePort(int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentException("port must be between 1 and 65535.");
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }
}

internal interface IReaderNetworkSessionFactory
{
    IReaderNetworkSession OpenSerial(VendorReaderOptions options, string lockFile);
    IReaderNetworkSession OpenTcp(string address, int port, int timeoutMilliseconds, string lockFile);
}

internal interface IReaderNetworkSession : IDisposable
{
    ReaderNetworkSettings Read();
    void Configure(ReaderNetworkSettings settings);
    void Reboot();
}

internal sealed class NativeReaderNetworkSessionFactory : IReaderNetworkSessionFactory
{
    public IReaderNetworkSession OpenSerial(VendorReaderOptions options, string lockFile)
    {
        NativeMethods.InstallResolver();
        var lease = ReaderDeviceLease.Acquire(lockFile);
        try
        {
            ThrowIfError(NativeMethods.ConnectSerial(options.SerialDevice, options.Baud, options.Rs485Address,
                options.TimeoutMilliseconds, out var handle), "connect serial");
            return new NativeReaderNetworkSession(ReaderSafeHandle.Create(handle), lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public IReaderNetworkSession OpenTcp(string address, int port, int timeoutMilliseconds, string lockFile)
    {
        NativeMethods.InstallResolver();
        var lease = ReaderDeviceLease.Acquire(lockFile);
        try
        {
            ThrowIfError(NativeMethods.ConnectNet(address, port, NativeTransport.Tcp, timeoutMilliseconds,
                out var handle), "connect TCP");
            return new NativeReaderNetworkSession(ReaderSafeHandle.Create(handle), lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    internal static void ThrowIfError(int result, string operation)
    {
        if (result != 0) throw new InvalidOperationException($"RFID SDK {operation} failed with code {result}.");
    }
}

internal sealed class NativeReaderNetworkSession(ReaderSafeHandle handle, ReaderDeviceLease lease) : IReaderNetworkSession
{
    private bool _disposed;

    public ReaderNetworkSettings Read()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = NativeMethods.GetNetworkParam(handle.Value, out var native);
        NativeReaderNetworkSessionFactory.ThrowIfError(result, "get_network_param");
        try
        {
            return new ReaderNetworkSettings(
                ReadRequired(native.Ip, "ip"),
                ReadRequired(native.Mask, "mask"),
                ReadRequired(native.Gateway, "gateway"),
                native.Port,
                ReadOptional(native.Dns));
        }
        finally
        {
            NativeMethods.FreeNetworkParam(ref native);
        }
    }

    public void Configure(ReaderNetworkSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        nint dns = nint.Zero;
        try
        {
            if (!string.IsNullOrWhiteSpace(settings.Dns)) dns = Marshal.StringToCoTaskMemUTF8(settings.Dns);
            NativeReaderNetworkSessionFactory.ThrowIfError(NativeMethods.SetNetworkParamWithDns(
                handle.Value, settings.Ip, settings.Mask, settings.Gateway, settings.Port, dns),
                "set_network_param_with_dns");
        }
        finally
        {
            if (dns != nint.Zero) Marshal.FreeCoTaskMem(dns);
        }
    }

    public void Reboot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeReaderNetworkSessionFactory.ThrowIfError(NativeMethods.Reboot(handle.Value), "reboot");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        handle.Dispose();
        lease.Dispose();
    }

    private static string ReadRequired(nint pointer, string name) =>
        ReadOptional(pointer) ?? throw new InvalidDataException($"Reader returned an empty {name} network value.");

    private static string? ReadOptional(nint pointer) =>
        pointer == nint.Zero ? null : Marshal.PtrToStringUTF8(pointer);
}
