using PeiwanAgent.Core.Models;

namespace PeiwanAgent.Core.Services;

public interface IWorkerClient : IAsyncDisposable
{
    event Action<WorkerEvent>? EventReceived;
    bool IsRunning { get; }
    Task StartRecordingAsync(WorkerCommand command, CancellationToken ct = default);
    Task SplitSegmentAsync(WorkerCommand command, CancellationToken ct = default);
    Task StopRecordingAsync(CancellationToken ct = default);
}
