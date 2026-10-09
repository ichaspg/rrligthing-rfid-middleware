using Microsoft.Extensions.Logging;
using System.Diagnostics;
using RfidMiddleware.Core;

namespace RfidMiddleware.Reader;

public sealed class ManagedFakeReader(ILogger<ManagedFakeReader> logger, IClock clock, FakeReaderOptions options) : IReaderAdapter
{
    private CancellationTokenSource? _inventoryCts;
    private Task? _pump;
    private ReaderHealth _health = new(false, false, null, clock.UtcNow);
    public ReaderHealth Health => Volatile.Read(ref _health);

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _health, new(true, false, null, clock.UtcNow));
        logger.LogInformation("Managed fake reader connected");
        return ValueTask.CompletedTask;
    }

    public ValueTask StartInventoryAsync(Func<RawTagRead, bool> onRead, CancellationToken cancellationToken = default)
    {
        if (!Health.Connected) throw new InvalidOperationException("Reader is not connected.");
        if (_pump is not null) return ValueTask.CompletedTask;
        _inventoryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _inventoryCts.Token;
        _pump = Task.Run(async () =>
        {
            var index = 0L;
            var rate = Math.Max(1, options.ReadsPerSecond);
            var started = Stopwatch.GetTimestamp();
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                var expected = (long)(Stopwatch.GetElapsedTime(started).TotalSeconds * rate);
                while (index < expected)
                {
                    var unauthorized = options.UnauthorizedEvery > 0 && index % options.UnauthorizedEvery == 0;
                    var epc = unauthorized
                        ? "E2801170FFFFFFFFFFFFFFFF"
                        : $"E2801170{index % Math.Max(1, options.AuthorizedEpcCount):X16}";
                    _ = onRead(new RawTagRead(epc, options.ReaderId, (int)(index % 4) + 1, -35 - (int)(index % 30), clock.UtcNow));
                    index++;
                }
            }
        }, token);
        Volatile.Write(ref _health, new(true, true, null, clock.UtcNow));
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopInventoryAsync(CancellationToken cancellationToken = default)
    {
        if (_inventoryCts is null) return;
        await _inventoryCts.CancelAsync().ConfigureAwait(false);
        try { if (_pump is not null) await _pump.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _inventoryCts.Dispose();
        _inventoryCts = null;
        _pump = null;
        Volatile.Write(ref _health, new(Health.Connected, false, null, clock.UtcNow));
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await StopInventoryAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _health, new(false, false, null, clock.UtcNow));
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);
}

public sealed class FakeReaderOptions
{
    public string ReaderId { get; set; } = "READER-01";
    public int ReadsPerSecond { get; set; } = 20;
    public int AuthorizedEpcCount { get; set; } = 100;
    public int UnauthorizedEvery { get; set; } = 25;
}
