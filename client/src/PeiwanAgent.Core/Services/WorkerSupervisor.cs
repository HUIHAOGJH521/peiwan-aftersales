using System.Diagnostics;
using System.Text.Json;
using PeiwanAgent.Core.Models;

namespace PeiwanAgent.Core.Services;

public sealed class WorkerSupervisor : IWorkerClient
{
    private readonly string _workerExePath;
    private readonly string _ffmpegPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private StreamWriter? _stdin;
    private Task? _stdoutReaderTask;
    private CancellationTokenSource? _readerCts;

    public event Action<WorkerEvent>? EventReceived;
    public bool IsRunning => _process is { HasExited: false };

    public WorkerSupervisor(string workerExePath, string ffmpegPath = "ffmpeg")
    {
        _workerExePath = workerExePath;
        _ffmpegPath = ffmpegPath;
    }

    public async Task StartRecordingAsync(WorkerCommand command, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureStartedLockedAsync().ConfigureAwait(false);
            await SendCommandLockedAsync(command with { Cmd = "START" }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SplitSegmentAsync(WorkerCommand command, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsRunning)
            {
                await EnsureStartedLockedAsync().ConfigureAwait(false);
                await SendCommandLockedAsync(command with { Cmd = "START" }, ct).ConfigureAwait(false);
                return;
            }
            await SendCommandLockedAsync(command with { Cmd = "SPLIT" }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopRecordingAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsRunning) return;

            try
            {
                await SendCommandLockedAsync(new WorkerCommand("STOP"), ct).ConfigureAwait(false);
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waitCts.CancelAfter(TimeSpan.FromSeconds(8));
                if (_process != null)
                {
                    await _process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                try { _process?.Kill(entireProcessTree: true); } catch { }
            }
            finally
            {
                CleanupProcessLocked();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task EnsureStartedLockedAsync()
    {
        if (IsRunning) return Task.CompletedTask;

        CleanupProcessLocked();

        var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _workerExePath,
                Arguments = $"--ffmpeg \"{_ffmpegPath}\"",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        if (!proc.Start())
        {
            throw new InvalidOperationException($"无法启动录制子进程: {_workerExePath}");
        }

        _process = proc;
        _stdin = proc.StandardInput;
        _stdin.AutoFlush = true;
        _readerCts = new CancellationTokenSource();
        var readerToken = _readerCts.Token;

        _stdoutReaderTask = Task.Run(async () =>
        {
            try
            {
                while (!readerToken.IsCancellationRequested)
                {
                    var line = await proc.StandardOutput.ReadLineAsync(readerToken).ConfigureAwait(false);
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        var evt = JsonSerializer.Deserialize<WorkerEvent>(line);
                        if (evt != null)
                        {
                            EventReceived?.Invoke(evt);
                        }
                    }
                    catch
                    {
                        // 忽略非 JSON 日志行
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, readerToken);

        _ = Task.Run(async () =>
        {
            try
            {
                while (!readerToken.IsCancellationRequested)
                {
                    var err = await proc.StandardError.ReadLineAsync(readerToken).ConfigureAwait(false);
                    if (err == null) break;
                }
            }
            catch { }
        }, readerToken);

        return Task.CompletedTask;
    }

    private async Task SendCommandLockedAsync(WorkerCommand command, CancellationToken ct)
    {
        if (_stdin == null) throw new InvalidOperationException("Worker stdin 未就绪");
        var json = JsonSerializer.Serialize(command);
        await _stdin.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
        await _stdin.FlushAsync(ct).ConfigureAwait(false);
    }

    private void CleanupProcessLocked()
    {
        try { _readerCts?.Cancel(); } catch { }
        try { _stdin?.Dispose(); } catch { }
        try { _process?.Dispose(); } catch { }
        _readerCts = null;
        _stdin = null;
        _process = null;
        _stdoutReaderTask = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopRecordingAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
