namespace RfidMiddleware.Reader.Vendor;

public enum VendorTransport { NativeMock, Tcp, TcpListener, Serial }
public enum VendorAlarmMode { Buzzer, DigitalOutput1, BuzzerAndDigitalOutput1 }

public sealed class VendorReaderOptions
{
    public VendorTransport Transport { get; set; } = VendorTransport.NativeMock;
    public string ReaderId { get; set; } = "READER-01";
    public string Address { get; set; } = "192.168.1.223";
    public int Port { get; set; } = 1969;
    public string SerialDevice { get; set; } = "/dev/serial/by-id/replace-me";
    public int Baud { get; set; } = 115200;
    public int Rs485Address { get; set; }
    public int TimeoutMilliseconds { get; set; } = 6000;
    public int MockPort { get; set; } = 26790;
    public int? AntennaPower { get; set; }
    public VendorAlarmMode AlarmMode { get; set; } = VendorAlarmMode.BuzzerAndDigitalOutput1;
    public int AlarmPulseMilliseconds { get; set; } = 1000;
}

public static class VendorReaderOptionsValidator
{
    public static void Validate(VendorReaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Transport))
            throw new ArgumentException("VendorReader.Transport is invalid.", nameof(options));
        if ((options.Transport is VendorTransport.Tcp or VendorTransport.TcpListener) &&
            options.Port is < 1 or > 65_535)
            throw new ArgumentException("VendorReader.Port must be between 1 and 65535.", nameof(options));
        if ((options.Transport is VendorTransport.Tcp or VendorTransport.Serial) &&
            options.TimeoutMilliseconds is < 100 or > 120_000)
            throw new ArgumentException("VendorReader.TimeoutMilliseconds must be between 100 and 120000.", nameof(options));
        if (options.AntennaPower is < 1 or > 33)
            throw new ArgumentException("VendorReader.AntennaPower must be between 1 and 33 dBm when configured.", nameof(options));
        if (options.AlarmPulseMilliseconds < 1)
            throw new ArgumentException("VendorReader.AlarmPulseMilliseconds must be positive.", nameof(options));
        if (options.Transport == VendorTransport.Tcp)
            _ = ReaderNetworkValidator.ParseIpv4(options.Address, "VendorReader.Address");
        if (options.Transport == VendorTransport.Serial && string.IsNullOrWhiteSpace(options.SerialDevice))
            throw new ArgumentException("VendorReader.SerialDevice is required for Serial transport.", nameof(options));
    }
}

