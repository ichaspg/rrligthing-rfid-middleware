namespace RfidMiddleware.Reader.Vendor;

public enum VendorTransport { NativeMock, Tcp, Serial }
public enum VendorAlarmMode { Buzzer, DigitalOutput0, DigitalOutput1, BuzzerAndDigitalOutputs }

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
    public VendorAlarmMode AlarmMode { get; set; } = VendorAlarmMode.BuzzerAndDigitalOutputs;
    public int AlarmPulseMilliseconds { get; set; } = 1000;
}

