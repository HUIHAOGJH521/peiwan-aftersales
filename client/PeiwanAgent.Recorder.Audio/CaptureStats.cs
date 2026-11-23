namespace PeiwanAgent.Recorder.Audio;

/// <summary>采集统计,用于校验时间轴。</summary>
public sealed record CaptureStats(
    DateTimeOffset StartWallClock,
    long SamplesWritten,
    long SilenceSamplesInserted,
    double FirstFrameOffsetMs,
    int DeviceRestarts,
    int SampleRate = AudioFormat.SampleRate)
{
    public double WrittenSeconds => (double)SamplesWritten / SampleRate;
}

public static class AudioFormat
{
    public const int SampleRate = 48000;
}
