using System.Text;

namespace RfidMiddleware.Reader.Vendor;

internal sealed class ReaderDeviceLease : IDisposable
{
    private readonly FileStream? _stream;

    private ReaderDeviceLease(FileStream? stream) => _stream = stream;

    internal static ReaderDeviceLease Acquire(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new ReaderDeviceLease(null);

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        try
        {
            var stream = new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            var owner = Encoding.UTF8.GetBytes($"pid={Environment.ProcessId}\nstarted={DateTimeOffset.UtcNow:O}\n");
            stream.Write(owner);
            stream.Flush(flushToDisk: true);
            return new ReaderDeviceLease(stream);
        }
        catch (IOException ex)
        {
            throw new ReaderDeviceBusyException(
                $"The RFID reader is already in use. Stop the middleware before commissioning. Lock: {fullPath}", ex);
        }
    }

    public void Dispose() => _stream?.Dispose();
}

public sealed class ReaderDeviceBusyException(string message, Exception? innerException = null)
    : IOException(message, innerException);
