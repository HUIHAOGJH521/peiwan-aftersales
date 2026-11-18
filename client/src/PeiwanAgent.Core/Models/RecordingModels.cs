using System.Text.Json.Serialization;

namespace PeiwanAgent.Core.Models;

public enum RecordingState
{
    Idle,
    RecordingPrivate,
    RecordingOrder,
    Paused
}

public enum CodecMode
{
    VideoHw,
    AudioScreenshots
}

public enum UploadStatus
{
    LocalOnly,
    Pending,
    Uploading,
    Paused,
    Completed,
    Failed
}

public sealed record DetectedProcessInfo(
    int ProcessId,
    string ProcessName,
    nint MainWindowHandle,
    string MainWindowTitle);

public sealed record OrderSessionRecord(
    string SessionId,
    string? OrderRef,
    string? Note,
    bool IsManualBackfill,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string Status);

public sealed record MediaSegmentRecord(
    string SegmentId,
    string? SessionId,
    int SeqNo,
    bool IsOrder,
    bool IsManualBackfill,
    string CodecMode,
    string FilePath,
    long FileSize,
    string Sha256,
    int DurationMs,
    UploadStatus UploadStatus,
    string? UploadId,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    DateTimeOffset ExpiresAt);

public sealed record WorkerCommand(
    [property: JsonPropertyName("cmd")] string Cmd,
    [property: JsonPropertyName("segment_id")] string? SegmentId = null,
    [property: JsonPropertyName("output_path")] string? OutputPath = null,
    [property: JsonPropertyName("codec_mode")] string? CodecMode = null,
    [property: JsonPropertyName("hw_encoder")] string? HwEncoder = null,
    [property: JsonPropertyName("target_hwnd")] long TargetHwnd = 0,
    [property: JsonPropertyName("fps")] int Fps = 15,
    [property: JsonPropertyName("bitrate_kbps")] int BitrateKbps = 850,
    [property: JsonPropertyName("is_order")] bool IsOrder = false,
    [property: JsonPropertyName("session_id")] string? SessionId = null,
    [property: JsonPropertyName("seq_no")] int SeqNo = 0);

public sealed record WorkerEvent(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("segment_id")] string? SegmentId = null,
    [property: JsonPropertyName("session_id")] string? SessionId = null,
    [property: JsonPropertyName("seq_no")] int SeqNo = 0,
    [property: JsonPropertyName("is_order")] bool IsOrder = false,
    [property: JsonPropertyName("codec_mode")] string? CodecMode = null,
    [property: JsonPropertyName("file_path")] string? FilePath = null,
    [property: JsonPropertyName("file_size")] long FileSize = 0,
    [property: JsonPropertyName("sha256")] string? Sha256 = null,
    [property: JsonPropertyName("duration_ms")] int DurationMs = 0,
    [property: JsonPropertyName("started_at")] DateTimeOffset? StartedAt = null,
    [property: JsonPropertyName("ended_at")] DateTimeOffset? EndedAt = null,
    [property: JsonPropertyName("message")] string? Message = null);
