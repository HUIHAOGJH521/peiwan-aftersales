namespace PeiwanAgent.Recorder.Audio;

/// <summary>按挂钟计算应补的静音样本数(纯逻辑)。</summary>
public sealed class SilencePadder
{
    private readonly int _rate;
    private readonly long _toleranceSamples;

    public SilencePadder(int sampleRate = AudioFormat.SampleRate, int toleranceMs = 60)
    {
        _rate = sampleRate;
        _toleranceSamples = (long)sampleRate * toleranceMs / 1000;
    }

    public long ExpectedSamples(TimeSpan elapsed) => (long)(elapsed.TotalSeconds * _rate);

    /// <summary>运行中:落后超过容差才补,补到与挂钟对齐。</summary>
    public long ComputePad(TimeSpan elapsed, long written)
    {
        long deficit = ExpectedSamples(elapsed) - written;
        return deficit > _toleranceSamples ? deficit : 0;
    }

    /// <summary>停止时:补到精确挂钟时长(不超出则为 0)。</summary>
    public long ComputeFinalPad(TimeSpan elapsed, long written)
        => Math.Max(0, ExpectedSamples(elapsed) - written);
}
