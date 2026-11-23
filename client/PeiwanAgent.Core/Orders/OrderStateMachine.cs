namespace PeiwanAgent.Core.Orders;

public enum RecordingMode { Private, Order }

public sealed record SplitRequestedEventArgs(
    RecordingMode PreviousMode,
    RecordingMode NewMode,
    string? SessionId,
    string? OrderRef,
    string? Note,
    DateTimeOffset At);

/// <summary>私人/接单两种模式。切换时发出 SplitRequested,让录制层封存当前切片(ADR 0003)。</summary>
public sealed class OrderStateMachine
{
    public RecordingMode Mode { get; private set; } = RecordingMode.Private;
    public string? CurrentSessionId { get; private set; }
    public string? CurrentOrderRef { get; private set; }
    public string? CurrentNote { get; private set; }

    public event EventHandler<SplitRequestedEventArgs>? SplitRequested;

    /// <summary>开始接单。已在接单中则忽略并返回 false(不重复切片)。订单号/备注可选。</summary>
    public bool StartOrder(DateTimeOffset now, string? orderRef = null, string? note = null, string? sessionId = null)
    {
        if (Mode == RecordingMode.Order) return false;
        CurrentSessionId = sessionId ?? Guid.NewGuid().ToString();
        CurrentOrderRef = Clean(orderRef);
        CurrentNote = Clean(note);
        Mode = RecordingMode.Order;
        SplitRequested?.Invoke(this, new SplitRequestedEventArgs(
            RecordingMode.Private, RecordingMode.Order, CurrentSessionId, CurrentOrderRef, CurrentNote, now));
        return true;
    }

    /// <summary>结束接单。不在接单中则忽略并返回 false。事件携带刚结束的会话信息。</summary>
    public bool EndOrder(DateTimeOffset now)
    {
        if (Mode == RecordingMode.Private) return false;
        var args = new SplitRequestedEventArgs(
            RecordingMode.Order, RecordingMode.Private, CurrentSessionId, CurrentOrderRef, CurrentNote, now);
        Mode = RecordingMode.Private;
        CurrentSessionId = CurrentOrderRef = CurrentNote = null;
        SplitRequested?.Invoke(this, args);
        return true;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}