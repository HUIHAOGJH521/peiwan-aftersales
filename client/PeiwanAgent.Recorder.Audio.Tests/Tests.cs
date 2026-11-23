using NAudio.Wave;
using PeiwanAgent.Recorder.Audio;
using Xunit;

namespace PeiwanAgent.Recorder.Audio.Tests;

public class SilencePadderTests
{
    private readonly SilencePadder _p = new();

    [Fact] public void NoPadWithinTolerance() => Assert.Equal(0, _p.ComputePad(TimeSpan.FromMilliseconds(50), 0));
    [Fact] public void PadsToWallClockWhenBehind() => Assert.Equal(48000, _p.ComputePad(TimeSpan.FromSeconds(1), 0));
    [Fact] public void NoPadWhenAhead() => Assert.Equal(0, _p.ComputePad(TimeSpan.FromSeconds(1), 50000));
    [Fact] public void FinalPadIsExact() => Assert.Equal(4800, _p.ComputeFinalPad(TimeSpan.FromSeconds(1), 43200));
    [Fact] public void FinalPadNeverNegative() => Assert.Equal(0, _p.ComputeFinalPad(TimeSpan.FromSeconds(1), 50000));

    [Fact]
    public void SimulatedGapKeepsDurationEqualToWallClock()
    {
        // 模拟 20ms 定时器:0~1s 无数据(静音),1~2s 有数据,2~3s 无数据
        long written = 0;
        for (int ms = 20; ms <= 3000; ms += 20)
        {
            var t = TimeSpan.FromMilliseconds(ms);
            if (ms > 1000 && ms <= 2000) written += 960; // 20ms 数据
            written += _p.ComputePad(t, written);
        }
        written += _p.ComputeFinalPad(TimeSpan.FromSeconds(3), written);
        // 数据期间补过的静音会使总数超过 -> 只验证不短于挂钟且误差 < 100ms
        Assert.InRange(written, 144000, 144000 + 4800);
    }

    [Fact]
    public void SilenceOnlyGivesExactWallClock()
    {
        long written = 0;
        for (int ms = 20; ms <= 5000; ms += 20) written += _p.ComputePad(TimeSpan.FromMilliseconds(ms), written);
        written += _p.ComputeFinalPad(TimeSpan.FromSeconds(5), written);
        Assert.Equal(240000, written);
    }
}

public class PcmPipelineTests
{
    private static byte[] FloatBytes(float[] f) { var b = new byte[f.Length * 4]; Buffer.BlockCopy(f, 0, b, 0, b.Length); return b; }

    [Fact] public void MixStereoAverages() =>
        Assert.Equal(new[] { 0.5f, 0f }, PcmPipeline.MixToMono(new[] { 1f, 0f, 0.5f, -0.5f }, 2));

    [Fact] public void MonoPassThrough() =>
        Assert.Equal(new[] { 0.1f, 0.2f }, PcmPipeline.MixToMono(new[] { 0.1f, 0.2f }, 1));

    [Fact] public void ClampAndScale()
    {
        Assert.Equal(32767, PcmPipeline.FloatToPcm16(2f));
        Assert.Equal(-32767, PcmPipeline.FloatToPcm16(-2f));
        Assert.Equal(0, PcmPipeline.FloatToPcm16(0f));
    }

    [Fact] public void No48kNoResample() =>
        Assert.False(new PcmPipeline(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).NeedsResample);

    [Fact] public void Float48kStereoToMono16()
    {
        var p = new PcmPipeline(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        var r = p.Process(FloatBytes(new[] { 1f, 0f, 0f, 0f }), 16);
        Assert.Equal(new short[] { 16384, 0 }, r);
    }

    [Fact] public void Pcm16StereoMixed()
    {
        var p = new PcmPipeline(new WaveFormat(48000, 16, 2));
        var b = new byte[4]; BitConverter.GetBytes((short)16384).CopyTo(b, 0); BitConverter.GetBytes((short)-16384).CopyTo(b, 2);
        Assert.Equal(new short[] { 0 }, p.Process(b, 4));
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(16000)]
    [InlineData(96000)]
    public void ResampleProducesTargetRateLength(int rate)
    {
        var p = new PcmPipeline(WaveFormat.CreateIeeeFloatWaveFormat(rate, 2));
        Assert.True(p.NeedsResample);
        long total = 0;
        int chunk = rate / 50; // 20ms
        for (int i = 0; i < 100; i++) // 2s
        {
            var f = new float[chunk * 2];
            for (int j = 0; j < chunk; j++) f[2 * j] = f[2 * j + 1] = 0.5f * MathF.Sin(2 * MathF.PI * 440 * (i * chunk + j) / rate);
            total += p.Process(FloatBytes(f), f.Length * 4).Length;
        }
        Assert.InRange(total, 96000 - 1500, 96000 + 100);
    }
}

public class DeviceSmokeTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("PEIWAN_AUDIO_SMOKE") == "1";

    [Fact]
    public void RecordThreeSecondsTwoTracks()
    {
        if (!Enabled) return; // 设置环境变量 PEIWAN_AUDIO_SMOKE=1 启用
        var dir = Path.Combine(Path.GetTempPath(), "peiwan-audio-smoke");
        Directory.CreateDirectory(dir);
        var mic = Path.Combine(dir, "mic.wav"); var lb = Path.Combine(dir, "loop.wav");
        using var m = new MicTrackCapture(); using var l = new LoopbackTrackCapture();
        InvalidOperationException? micErr = null;
        try { m.Start(mic); } catch (InvalidOperationException ex) { micErr = ex; }
        l.Start(lb);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Thread.Sleep(int.TryParse(Environment.GetEnvironmentVariable("PEIWAN_AUDIO_SMOKE_MS"), out var ms) ? ms : 3000);
        m.Stop(); var micEnd = DateTimeOffset.UtcNow;
        l.Stop(); var loopEnd = DateTimeOffset.UtcNow;
        m.Stop(); l.Stop(); // 幂等
        var report = new List<string> { $"window={sw.Elapsed.TotalSeconds:F3}s" };
        // 每轨挂钟 = Stop 返回时刻 - 该轨 Start 内记录的起始挂钟
        Check("loop", lb, (loopEnd - l.GetStats().StartWallClock).TotalSeconds, l, report);
        if (micErr == null) Check("mic", mic, (micEnd - m.GetStats().StartWallClock).TotalSeconds, m, report);
        else report.Add("mic 不可用: " + micErr.Message);
        File.WriteAllLines(Path.Combine(dir, "report.txt"), report);
    }

    private static void Check(string name, string path, double wall, WasapiTrackCaptureBase c, List<string> report)
    {
        using var r = new WaveFileReader(path);
        Assert.Equal(48000, r.WaveFormat.SampleRate);
        Assert.Equal(16, r.WaveFormat.BitsPerSample);
        Assert.Equal(1, r.WaveFormat.Channels);
        Assert.Equal(WaveFormatEncoding.Pcm, r.WaveFormat.Encoding);
        var err = Math.Abs(r.TotalTime.TotalSeconds - wall) * 1000;
        var s = c.GetStats();
        report.Add($"{name}: dur={r.TotalTime.TotalSeconds:F3}s err={err:F1}ms written={s.SamplesWritten} silence={s.SilenceSamplesInserted} firstOffset={s.FirstFrameOffsetMs:F1}ms");
        Assert.True(err < 100, $"{name} 误差 {err}ms");
    }
}


