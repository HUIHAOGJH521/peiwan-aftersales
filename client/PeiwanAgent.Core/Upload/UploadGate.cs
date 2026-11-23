namespace PeiwanAgent.Core.Upload;

public enum GateDecision
{
    /// <summary>游戏运行中,一律挂起。</summary>
    SuspendedGameRunning,
    /// <summary>已退游,等待延迟或闲置条件。</summary>
    WaitingAfterExit,
    /// <summary>放行。</summary>
    Open
}

/// <summary>
/// 后台上传闸门(ADR 0003、architecture 3.2)。
/// 游戏运行 → 挂起;退游后 post_exit_delay_sec 已过 或 系统闲置 ≥ idle_threshold_sec → 放行。
/// 若从未观察到游戏运行(例如客户端重启时游戏已关),视为无需保护,放行。
/// </summary>
public sealed class UploadGate
{
    private readonly TimeSpan _postExitDelay;
    private readonly TimeSpan _idleThreshold;
    private DateTimeOffset? _exitedAt;

    public UploadGate(int postExitDelaySec = 30, int idleThresholdSec = 60)
    {
        if (postExitDelaySec < 0) throw new ArgumentOutOfRangeException(nameof(postExitDelaySec));
        if (idleThresholdSec < 0) throw new ArgumentOutOfRangeException(nameof(idleThresholdSec));
        _postExitDelay = TimeSpan.FromSeconds(postExitDelaySec);
        _idleThreshold = TimeSpan.FromSeconds(idleThresholdSec);
    }

    public bool IsGameRunning { get; private set; }

    public void NotifyGameStarted()
    {
        IsGameRunning = true;
        _exitedAt = null;
    }

    public void NotifyGameExited(DateTimeOffset now)
    {
        IsGameRunning = false;
        _exitedAt = now;
    }

    public GateDecision Evaluate(DateTimeOffset now, TimeSpan systemIdle)
    {
        if (IsGameRunning) return GateDecision.SuspendedGameRunning;
        if (_exitedAt is null) return GateDecision.Open;
        if (now - _exitedAt.Value >= _postExitDelay || systemIdle >= _idleThreshold) return GateDecision.Open;
        return GateDecision.WaitingAfterExit;
    }

    /// <summary>私人局切片永不入队。</summary>
    public static bool ShouldEnqueue(bool isOrderSegment, bool isManualBackfill = false)
        => isOrderSegment || isManualBackfill;

    /// <summary>该切片此刻是否可以开始/继续上传。</summary>
    public bool CanUpload(bool isOrderSegment, bool isManualBackfill, DateTimeOffset now, TimeSpan systemIdle)
        => ShouldEnqueue(isOrderSegment, isManualBackfill) && Evaluate(now, systemIdle) == GateDecision.Open;
}