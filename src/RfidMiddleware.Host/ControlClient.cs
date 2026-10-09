using System.Net.Sockets;
using System.Text;

namespace RfidMiddleware.Host;

internal sealed class ControlClient(string socketPath) : IDisposable
{
    private readonly HttpClient _http = Create(socketPath);

    internal async Task<string> GetAsync(string path, CancellationToken token = default) =>
        await _http.GetStringAsync(path, token);

    internal async Task<string> PostAsync(string path, CancellationToken token = default)
    {
        using var response = await _http.PostAsync(path, new StringContent("{}", Encoding.UTF8, "application/json"), token);
        var body = await response.Content.ReadAsStringAsync(token);
        response.EnsureSuccessStatusCode();
        return body;
    }

    private static HttpClient Create(string path)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), token);
                    return new NetworkStream(socket, true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
    }

    public void Dispose() => _http.Dispose();
}

