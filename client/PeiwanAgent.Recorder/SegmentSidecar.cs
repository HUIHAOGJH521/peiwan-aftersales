using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeiwanAgent.Recorder;

public static class CodecModes
{
    public const string VideoHardware = "video_hw";
    public const string VideoSoftware = "video_sw";
    public const string AudioScreenshots = "audio_screenshots";
    public const string AudioOnly = "audio_only";
}

public sealed record TrackInfo(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("duration_s")] double? DurationSeconds);

/// <summary>切片旁的 {segment_id}.json。</summary>
public sealed record SegmentSidecar
{
    [JsonPropertyName("segment_id")] public string SegmentId { get; init; } = "";
    [JsonPropertyName("session_id")] public string? SessionId { get; init; }
    [JsonPropertyName("is_order")] public bool IsOrder { get; init; }
    [JsonPropertyName("started_at")] public DateTimeOffset StartedAt { get; init; }
    [JsonPropertyName("ended_at")] public DateTimeOffset EndedAt { get; init; }
    [JsonPropertyName("codec_mode")] public string CodecMode { get; init; } = "";
    [JsonPropertyName("video_encoder")] public string? VideoEncoder { get; init; }
    [JsonPropertyName("file")] public string? File { get; init; }
    [JsonPropertyName("video_duration_s")] public double? VideoDurationSeconds { get; init; }
    [JsonPropertyName("tracks")] public List<TrackInfo> Tracks { get; init; } = [];
    [JsonPropertyName("screenshots_dir")] public string? ScreenshotsDir { get; init; }
    [JsonPropertyName("screenshot_count")] public int? ScreenshotCount { get; init; }
    [JsonPropertyName("screenshot_interval_s")] public int? ScreenshotIntervalSeconds { get; init; }
    [JsonPropertyName("warnings")] public List<string> Warnings { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static SegmentSidecar? FromJson(string json) => JsonSerializer.Deserialize<SegmentSidecar>(json, Json);
}
