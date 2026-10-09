namespace RfidMiddleware.Server;

public sealed class ServerOptions
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:5080";
    public string MiddlewareId { get; set; } = "WH-A-GATE-01";
    public string TokenEnvironmentVariable { get; set; } = "RFID_API_TOKEN";
    public int TimeoutSeconds { get; set; } = 10;
}

