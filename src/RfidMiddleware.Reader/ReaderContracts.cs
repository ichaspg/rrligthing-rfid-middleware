using RfidMiddleware.Core;

namespace RfidMiddleware.Reader;

public interface IReaderAdapter : IAsyncDisposable
{
    ReaderHealth Health { get; }
    ValueTask ConnectAsync(CancellationToken cancellationToken = default);
    ValueTask StartInventoryAsync(Func<RawTagRead, bool> onRead, CancellationToken cancellationToken = default);
    ValueTask StopInventoryAsync(CancellationToken cancellationToken = default);
    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);
}

public interface IAlarmController
{
    AlarmHealth Health { get; }
    ValueTask SignalUnauthorizedAsync(CancellationToken cancellationToken = default);
    ValueTask SignalSystemFaultAsync(CancellationToken cancellationToken = default);
    ValueTask ResetAsync(CancellationToken cancellationToken = default);
}

