using System.Text;
using System.Text.Json;

namespace RfidMiddleware.Reader.Vendor;

public sealed record ReaderClientTargetSettings(string ServerIp, int Port, bool Enabled);

public sealed record ReaderClientReportSettings(
    ReaderClientTargetSettings Udp,
    ReaderClientTargetSettings Tcp);

public sealed record ReaderClientCommissioningResult(
    ReaderClientReportSettings Current,
    ReaderClientReportSettings Proposed,
    ReaderClientReportSettings? ReadBack,
    bool Applied,
    bool Rebooted,
    string? BackupFile);

public sealed class ReaderClientCommissioner
{
    private readonly IReaderClientSessionFactory _sessions;
    private readonly Func<DateTimeOffset> _utcNow;

    public ReaderClientCommissioner() : this(new NativeReaderClientSessionFactory(), () => DateTimeOffset.UtcNow) { }

    internal ReaderClientCommissioner(IReaderClientSessionFactory sessions, Func<DateTimeOffset> utcNow)
    {
        _sessions = sessions;
        _utcNow = utcNow;
    }

    public ReaderClientReportSettings ShowSerial(VendorReaderOptions options, string lockFile)
    {
        ValidateSerialOptions(options);
        using var session = _sessions.OpenSerial(options, lockFile);
        return Normalize(session.Read());
    }

    public ReaderClientCommissioningResult ConfigureSerial(
        VendorReaderOptions options,
        string lockFile,
        ReaderClientTargetSettings target,
        bool apply,
        string backupDirectory)
    {
        ValidateSerialOptions(options);
        var normalizedTarget = ReaderClientTargetValidator.ValidateEnabled(target);
        using var session = _sessions.OpenSerial(options, lockFile);
        var current = Normalize(session.Read());
        var proposed = current with { Tcp = normalizedTarget };
        return Apply(session, options, current, proposed, apply, backupDirectory);
    }

    public ReaderClientCommissioningResult DisableSerial(
        VendorReaderOptions options,
        string lockFile,
        bool apply,
        string backupDirectory)
    {
        ValidateSerialOptions(options);
        using var session = _sessions.OpenSerial(options, lockFile);
        var current = Normalize(session.Read());
        var proposed = current with { Tcp = current.Tcp with { Enabled = false } };
        return Apply(session, options, current, proposed, apply, backupDirectory);
    }

    private ReaderClientCommissioningResult Apply(
        IReaderClientSession session,
        VendorReaderOptions options,
        ReaderClientReportSettings current,
        ReaderClientReportSettings proposed,
        bool apply,
        string backupDirectory)
    {
        if (!apply)
            return new ReaderClientCommissioningResult(current, proposed, null, false, false, null);

        var backupFile = SaveBackup(backupDirectory, options, current);
        session.Configure(proposed);
        var readBack = Normalize(session.Read());
        if (readBack != proposed)
            throw new ReaderNetworkVerificationException(
                $"Reader client-mode read-back did not match the requested configuration. Backup: {backupFile}");

        session.Reboot();
        return new ReaderClientCommissioningResult(current, proposed, readBack, true, true, backupFile);
    }

    private string SaveBackup(string directory, VendorReaderOptions options, ReaderClientReportSettings current)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Commissioning backup directory is required.", nameof(directory));

        var fullDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullDirectory);
        var stamp = _utcNow().ToString("yyyyMMddTHHmmssfffZ");
        var finalPath = Path.Combine(fullDirectory, $"reader-client-{stamp}-{Guid.NewGuid():N}.json");
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

    private static ReaderClientReportSettings Normalize(ReaderClientReportSettings settings) => new(
        Normalize(settings.Udp),
        Normalize(settings.Tcp));

    private static ReaderClientTargetSettings Normalize(ReaderClientTargetSettings settings) =>
        new(settings.ServerIp.Trim(), settings.Port, settings.Enabled);

    private static void ValidateSerialOptions(VendorReaderOptions options)
    {
        if (options.Transport != VendorTransport.Serial)
            throw new ArgumentException("Reader client commissioning requires VendorReader.Transport=Serial.");
        if (string.IsNullOrWhiteSpace(options.SerialDevice))
            throw new ArgumentException("VendorReader.SerialDevice is required.");
        if (options.Baud != 115200)
            throw new ArgumentException("BAFO RS-232 commissioning requires baud 115200.");
        if (options.Rs485Address != 0)
            throw new ArgumentException("BAFO RS-232 commissioning requires reader address 0.");
    }
}

internal static class ReaderClientTargetValidator
{
    internal static ReaderClientTargetSettings ValidateEnabled(ReaderClientTargetSettings target)
    {
        if (!target.Enabled) throw new ArgumentException("Configured reader TCP client target must be enabled.");
        var address = ReaderNetworkValidator.ParseIpv4(target.ServerIp, "server-ip");
        ReaderNetworkValidator.ValidatePort(target.Port);
        return new ReaderClientTargetSettings(address.ToString(), target.Port, true);
    }
}

internal interface IReaderClientSessionFactory
{
    IReaderClientSession OpenSerial(VendorReaderOptions options, string lockFile);
}

internal interface IReaderClientSession : IDisposable
{
    ReaderClientReportSettings Read();
    void Configure(ReaderClientReportSettings settings);
    void Reboot();
}

internal sealed class NativeReaderClientSessionFactory : IReaderClientSessionFactory
{
    public IReaderClientSession OpenSerial(VendorReaderOptions options, string lockFile)
    {
        NativeMethods.InstallResolver();
        var lease = ReaderDeviceLease.Acquire(lockFile);
        try
        {
            NativeReaderNetworkSessionFactory.ThrowIfError(
                NativeMethods.ConnectSerial(options.SerialDevice, options.Baud, options.Rs485Address,
                    options.TimeoutMilliseconds, out var handle),
                "connect serial");
            return new NativeReaderClientSession(ReaderSafeHandle.Create(handle), lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }
}

internal sealed unsafe class NativeReaderClientSession(ReaderSafeHandle handle, ReaderDeviceLease lease)
    : IReaderClientSession
{
    public ReaderClientReportSettings Read()
    {
        NativeReaderNetworkSessionFactory.ThrowIfError(
            NativeMethods.GetReportOptions(handle.Value, out var udp, out var tcp),
            "get_report_option");
        return new ReaderClientReportSettings(FromNative(udp), FromNative(tcp));
    }

    public void Configure(ReaderClientReportSettings settings)
    {
        var udp = ToNative(settings.Udp);
        var tcp = ToNative(settings.Tcp);
        NativeReaderNetworkSessionFactory.ThrowIfError(
            NativeMethods.SetReportOptions(handle.Value, udp, tcp),
            "set_report_option");
    }

    public void Reboot() => NativeReaderNetworkSessionFactory.ThrowIfError(
        NativeMethods.Reboot(handle.Value), "reboot");

    public void Dispose()
    {
        handle.Dispose();
        lease.Dispose();
    }

    private static ReaderClientTargetSettings FromNative(NativeReportOption option)
    {
        var pointer = option.Ip;
        var length = 0;
        while (length < 64 && pointer[length] != 0) length++;
        return new ReaderClientTargetSettings(
            Encoding.UTF8.GetString(new ReadOnlySpan<byte>(pointer, length)),
            option.Port,
            option.Enable != 0);
    }

    private static NativeReportOption ToNative(ReaderClientTargetSettings settings)
    {
        var encoded = Encoding.UTF8.GetBytes(settings.ServerIp);
        if (encoded.Length > 63) throw new ArgumentException("Reader report target address exceeds 63 bytes.");

        var option = new NativeReportOption
        {
            Port = settings.Port,
            Enable = settings.Enabled ? 1 : 0
        };
        var destination = option.Ip;
        new Span<byte>(destination, 64).Clear();
        encoded.CopyTo(new Span<byte>(destination, 63));
        return option;
    }
}
