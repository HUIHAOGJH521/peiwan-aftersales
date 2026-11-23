namespace PeiwanAgent.Core.Recording;

public sealed record SegmentInfo(string SegmentId, string? SessionId, bool IsOrder, string FilePath,
    DateTimeOffset StartedAt, DateTimeOffset EndedAt);

/// <summary>录制接口。实现只能用 WGC/WASAPI 等系统公开接口,严禁注入/钩子。</summary>
public interface IRecorder
{
    void Start(bool isOrder, string? sessionId);
    /// <summary>封存当前切片并以新参数开始下一片。</summary>
    SegmentInfo Split(bool nextIsOrder, string? nextSessionId);
    SegmentInfo? Stop();
}