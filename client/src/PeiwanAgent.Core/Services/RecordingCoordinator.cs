using PeiwanAgent.Core.Config;
using PeiwanAgent.Core.Models;
using PeiwanAgent.Core.Storage;

namespace PeiwanAgent.Core.Services;

public sealed class RecordingCoordinator : IAsyncDisposable
{
    private readonly AgentConfig _config;
    private readonly LocalStore _store;
    private readonly IProcessMonitor _processMonitor;
    private readonly IEncoderProbe _encoderProbe;
    private readonly IWorkerClient _worker;
    private readonly RetentionCleaner _cleaner;
    private readonly SemaphoreSlim _stateGate = new(1, 1);

    private EncoderProbeResult? _encoderResult;
    private DetectedProcessInfo? _activeGame;
    private string? _activeSessionId;
    private int _currentOrderSeqNo;
    private string? _pendingOrderRef;
    private string? _pendingOrderNote;
    private bool _pendingOrderActive;
    private DateTimeOffset _currentSegmentStartedAt;
    private DateTimeOffset _lastCleanupAt = DateTimeOffset.MinValue;

    public RecordingState State { get; private set; } = RecordingState.Idle;
    public string? ActiveSessionId => _activeSessionId;
    public string? ActiveOrderRef { get; private set; }
    public string? ActiveOrderNote { get; private set; }
    public EncoderProbeResult? EncoderInfo => _encoderResult;
    public DetectedProcessInfo? ActiveGame => _activeGame;

    public event Action<RecordingState>? StateChanged;
    public event Action<MediaSegmentRecord>? SegmentCompleted;
    public event Action<string>? StatusMessage;

    public RecordingCoordinator(
        AgentConfig config,
        LocalStore store,
        IProcessMonitor processMonitor,
        IEncoderProbe encoderProbe,
        IWorkerClient worker)
    {
        _config = config;
        _store = store;
        _processMonitor = processMonitor;
        _encoderProbe = encoderProbe;
        _worker = worker;
        _cleaner = new RetentionCleaner(store);

        Directory.CreateDirectory(_config.RecordingsDir);
        _worker.EventReceived += OnWorkerEvent;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _encoderResult = await _encoderProbe.ProbeBestEncoderAsync(_config.FfmpegPath, ct).ConfigureAwait(false);
        var modeDesc = _encoderResult.Mode == CodecMode.VideoHw
            ? $"硬件编码 ({_encoderResult.HwEncoderName})"
            : "降级模式 (双音轨 + 低帧率截图)";
        StatusMessage?.Invoke($"编码探测完成：{modeDesc}");
    }

    /// <summary>
    /// 供定时调度器调用一次状态巡检（检测游戏启停、5分钟滚动切段、3天缓存清理）
    /// </summary>
    public async Task TickAsync(DateTimeOffset? nowOverride = null, CancellationToken ct = default)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        await _stateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_encoderResult == null)
            {
                await InitializeAsync(ct).ConfigureAwait(false);
            }

            if (now - _lastCleanupAt > TimeSpan.FromMinutes(15))
            {
                var removed = _cleaner.CleanExpired(now);
                _lastCleanupAt = now;
                if (removed > 0)
                {
                    StatusMessage?.Invoke($"已清理 {removed} 个过期本地缓存切片");
                }
            }

            var detected = _processMonitor.FindRunningGame(_config.TargetProcessNames);
            if (detected != null && State == RecordingState.Idle)
            {
                _activeGame = detected;
                if (_pendingOrderActive)
                {
                    await BeginOrderLockedAsync(_pendingOrderRef, _pendingOrderNote, now, startNewWorker: true, ct).ConfigureAwait(false);
                }
                else
                {
                    await BeginPrivateLockedAsync(now, startNewWorker: true, ct).ConfigureAwait(false);
                }
            }
            else if (detected == null && State != RecordingState.Idle)
            {
                _activeGame = null;
                await StopAllRecordingLockedAsync(now, ct).ConfigureAwait(false);
            }
            else if (detected != null && (State == RecordingState.RecordingPrivate || State == RecordingState.RecordingOrder))
            {
                _activeGame = detected;
                if ((now - _currentSegmentStartedAt).TotalSeconds >= _config.SegmentDurationSeconds)
                {
                    await RollSegmentLockedAsync(now, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task StartOrderAsync(string? orderRef, string? note, DateTimeOffset? nowOverride = null, CancellationToken ct = default)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        await _stateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State == RecordingState.RecordingOrder)
            {
                return;
            }

            if (State == RecordingState.Idle || State == RecordingState.Paused)
            {
                // 若游戏尚未启动或处于暂停态，记录接单意图；若检测到游戏在跑，立即恢复并切入接单态
                _pendingOrderActive = true;
                _pendingOrderRef = string.IsNullOrWhiteSpace(orderRef) ? null : orderRef.Trim();
                _pendingOrderNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
                ActiveOrderRef = _pendingOrderRef;
                ActiveOrderNote = _pendingOrderNote;

                if (State == RecordingState.Paused && _activeGame != null)
                {
                    await BeginOrderLockedAsync(_pendingOrderRef, _pendingOrderNote, now, startNewWorker: true, ct).ConfigureAwait(false);
                }
                else
                {
                    StatusMessage?.Invoke("已开启接单状态，检测到游戏运行后将自动进入接单录制");
                }
                return;
            }

            // 当前为 RecordingPrivate -> 立即 Split 物理切段隔离私人局与接单局
            await BeginOrderLockedAsync(orderRef, note, now, startNewWorker: false, ct).ConfigureAwait(false);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task EndOrderAsync(DateTimeOffset? nowOverride = null, CancellationToken ct = default)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        await _stateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _pendingOrderActive = false;
            _pendingOrderRef = null;
            _pendingOrderNote = null;

            if (State != RecordingState.RecordingOrder)
            {
                ActiveOrderRef = null;
                ActiveOrderNote = null;
                return;
            }

            if (_activeSessionId != null)
            {
                _store.FinishSession(_activeSessionId, now);
            }

            _activeSessionId = null;
            _currentOrderSeqNo = 0;
            ActiveOrderRef = null;
            ActiveOrderNote = null;

            if (_activeGame != null)
            {
                // 游戏仍在运行：立即 Split 切回私人本地兜底录制
                await BeginPrivateLockedAsync(now, startNewWorker: false, ct).ConfigureAwait(false);
            }
            else
            {
                await StopAllRecordingLockedAsync(now, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task PauseAsync(DateTimeOffset? nowOverride = null, CancellationToken ct = default)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        await _stateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State is RecordingState.Idle or RecordingState.Paused) return;

            if (State == RecordingState.RecordingOrder && _activeSessionId != null)
            {
                _store.FinishSession(_activeSessionId, now);
                _activeSessionId = null;
                _pendingOrderActive = false;
                ActiveOrderRef = null;
                ActiveOrderNote = null;
            }

            await _worker.StopRecordingAsync(ct).ConfigureAwait(false);
            SetState(RecordingState.Paused);
            StatusMessage?.Invoke("录制已手动暂停");
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task ResumeAsync(DateTimeOffset? nowOverride = null, CancellationToken ct = default)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        await _stateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State != RecordingState.Paused) return;

            var detected = _processMonitor.FindRunningGame(_config.TargetProcessNames);
            if (detected == null)
            {
                _activeGame = null;
                SetState(RecordingState.Idle);
                return;
            }

            _activeGame = detected;
            await BeginPrivateLockedAsync(now, startNewWorker: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public string RequestManualBackfill(
        IReadOnlyList<string> segmentIds,
        string? orderRef,
        string? note,
        DateTimeOffset? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        var sessionId = Guid.NewGuid().ToString();
        _store.MarkSegmentsForManualBackfill(
            segmentIds,
            sessionId,
            string.IsNullOrWhiteSpace(orderRef) ? null : orderRef.Trim(),
            string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            now);
        StatusMessage?.Invoke($"已授权补传 {segmentIds.Count} 个本地片段，将于空闲时上传");
        return sessionId;
    }

    private async Task BeginPrivateLockedAsync(DateTimeOffset now, bool startNewWorker, CancellationToken ct)
    {
        var segmentId = Guid.NewGuid().ToString();
        var cmd = BuildWorkerCommand(startNewWorker ? "START" : "SPLIT", segmentId, isOrder: false, sessionId: null, seqNo: 0, now: now);
        _currentSegmentStartedAt = now;

        if (startNewWorker)
        {
            await _worker.StartRecordingAsync(cmd, ct).ConfigureAwait(false);
        }
        else
        {
            await _worker.SplitSegmentAsync(cmd, ct).ConfigureAwait(false);
        }

        SetState(RecordingState.RecordingPrivate);
        StatusMessage?.Invoke("检测到游戏运行：私人本地兜底录制中（不上云，保留3天）");
    }

    private async Task BeginOrderLockedAsync(string? orderRef, string? note, DateTimeOffset now, bool startNewWorker, CancellationToken ct)
    {
        var cleanRef = string.IsNullOrWhiteSpace(orderRef) ? null : orderRef.Trim();
        var cleanNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        var sessionId = Guid.NewGuid().ToString();
        _activeSessionId = sessionId;
        _currentOrderSeqNo = 1;
        ActiveOrderRef = cleanRef;
        ActiveOrderNote = cleanNote;
        _pendingOrderActive = false;

        _store.UpsertSession(new OrderSessionRecord(
            SessionId: sessionId,
            OrderRef: cleanRef,
            Note: cleanNote,
            IsManualBackfill: false,
            StartedAt: now,
            EndedAt: null,
            Status: "recording"));

        var segmentId = Guid.NewGuid().ToString();
        var cmd = BuildWorkerCommand(startNewWorker ? "START" : "SPLIT", segmentId, isOrder: true, sessionId: sessionId, seqNo: _currentOrderSeqNo, now: now);
        _currentSegmentStartedAt = now;

        if (startNewWorker)
        {
            await _worker.StartRecordingAsync(cmd, ct).ConfigureAwait(false);
        }
        else
        {
            await _worker.SplitSegmentAsync(cmd, ct).ConfigureAwait(false);
        }

        SetState(RecordingState.RecordingOrder);
        StatusMessage?.Invoke($"接单录制中（会话 {sessionId[..8]}），切段已隔离");
    }

    private async Task RollSegmentLockedAsync(DateTimeOffset now, CancellationToken ct)
    {
        var isOrder = State == RecordingState.RecordingOrder;
        if (isOrder)
        {
            _currentOrderSeqNo++;
        }

        var segmentId = Guid.NewGuid().ToString();
        var cmd = BuildWorkerCommand(
            "SPLIT",
            segmentId,
            isOrder: isOrder,
            sessionId: isOrder ? _activeSessionId : null,
            seqNo: isOrder ? _currentOrderSeqNo : 0,
            now: now);

        _currentSegmentStartedAt = now;
        await _worker.SplitSegmentAsync(cmd, ct).ConfigureAwait(false);
    }

    private async Task StopAllRecordingLockedAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (State == RecordingState.RecordingOrder && _activeSessionId != null)
        {
            _store.FinishSession(_activeSessionId, now);
            _activeSessionId = null;
            _currentOrderSeqNo = 0;
            ActiveOrderRef = null;
            ActiveOrderNote = null;
        }

        await _worker.StopRecordingAsync(ct).ConfigureAwait(false);
        SetState(RecordingState.Idle);
        StatusMessage?.Invoke("游戏已退出，录制停止");
    }

    private WorkerCommand BuildWorkerCommand(
        string cmd,
        string segmentId,
        bool isOrder,
        string? sessionId,
        int seqNo,
        DateTimeOffset now)
    {
        var probe = _encoderResult ?? new EncoderProbeResult(CodecMode.AudioScreenshots, null);
        var modeStr = probe.Mode == CodecMode.VideoHw ? "video_hw" : "audio_screenshots";
        var dayDir = Path.Combine(_config.RecordingsDir, now.LocalDateTime.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dayDir);

        var ext = probe.Mode == CodecMode.VideoHw ? "mp4" : "m4a";
        var prefix = isOrder ? "order" : "private";
        var filePath = Path.Combine(dayDir, $"{now.LocalDateTime:HHmmss}_{prefix}_{segmentId}.{ext}");

        return new WorkerCommand(
            Cmd: cmd,
            SegmentId: segmentId,
            OutputPath: filePath,
            CodecMode: modeStr,
            HwEncoder: probe.HwEncoderName,
            TargetHwnd: _activeGame?.MainWindowHandle ?? 0,
            Fps: _config.TargetFps,
            BitrateKbps: _config.VideoBitrateKbps,
            IsOrder: isOrder,
            SessionId: sessionId,
            SeqNo: seqNo);
    }

    private void OnWorkerEvent(WorkerEvent evt)
    {
        if (evt.Event != "SEGMENT_CLOSED" || string.IsNullOrWhiteSpace(evt.SegmentId) || string.IsNullOrWhiteSpace(evt.FilePath))
        {
            if (evt.Event == "ERROR" && !string.IsNullOrWhiteSpace(evt.Message))
            {
                StatusMessage?.Invoke($"录制子进程提示：{evt.Message}");
            }
            return;
        }

        var startedAt = evt.StartedAt ?? DateTimeOffset.UtcNow;
        var endedAt = evt.EndedAt ?? DateTimeOffset.UtcNow;
        var record = new MediaSegmentRecord(
            SegmentId: evt.SegmentId,
            SessionId: evt.SessionId,
            SeqNo: evt.SeqNo,
            IsOrder: evt.IsOrder,
            IsManualBackfill: false,
            CodecMode: evt.CodecMode ?? "video_hw",
            FilePath: evt.FilePath,
            FileSize: evt.FileSize,
            Sha256: evt.Sha256 ?? string.Empty,
            DurationMs: evt.DurationMs,
            UploadStatus: evt.IsOrder ? UploadStatus.Pending : UploadStatus.LocalOnly,
            UploadId: null,
            StartedAt: startedAt,
            EndedAt: endedAt,
            ExpiresAt: endedAt.AddDays(_config.LocalRetentionDays));

        _store.InsertSegment(record);
        SegmentCompleted?.Invoke(record);
    }

    private void SetState(RecordingState next)
    {
        if (State == next) return;
        State = next;
        StateChanged?.Invoke(next);
    }

    public async ValueTask DisposeAsync()
    {
        _worker.EventReceived -= OnWorkerEvent;
        await _worker.DisposeAsync().ConfigureAwait(false);
        _stateGate.Dispose();
    }
}
