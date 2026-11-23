using System.Diagnostics;
using System.Text.Json;
using PeiwanAgent.Core.Recording;
using PeiwanAgent.Recorder.Ffmpeg;

namespace PeiwanAgent.Recorder.Tests;

/// <summary>仅当环境变量 PEIWAN_RECORDER_SMOKE=1 时运行(需要本机 ffmpeg/ffprobe 与桌面会话)。</summary>
public sealed class SmokeFactAttribute : FactAttribute
{
    public SmokeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PEIWAN_RECORDER_SMOKE") != "1")
            Skip = "设置 PEIWAN_RECORDER_SMOKE=1 以运行手动冒烟测试";
    }
}

/// <summary>假音轨:Stop 时用 ffmpeg 生成与录制时长相同的正弦 WAV。</summary>
internal sealed class FfmpegToneTrack(string ffmpeg, int hz) : ITrackCapture
{
    private string? _path;
    private DateTime _t0;
    public void Start(string wavPath) { _path = wavPath; _t0 = DateTime.UtcNow; }
    public void Stop()
    {
        if (_path is null) return;
        var secs = (DateTime.UtcNow - _t0).TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var r = new FfmpegRunner(ffmpeg).Run(["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
            $"sine=frequency={hz}:sample_rate=48000", "-t", secs, "-c:a", "pcm_s16le", _path], TimeSpan.FromSeconds(30));
        if (!r.Success) throw new InvalidOperationException(r.Stderr);
        _path = null;
    }
    public void Dispose() { }
}

public class RecorderSmokeTests
{
    // mp4 封装器把流 title 写成 "name" 原子,ffprobe 读出为 name;handler_name 同时写入。
    private static string? Title(JsonElement s)
    {
        var tags = s.GetProperty("tags");
        foreach (var k in new[] { "title", "name", "handler_name" })
            if (tags.TryGetProperty(k, out var v) && v.GetString() is { Length: > 0 } t && t != "SoundHandler") return t;
        return null;
    }

    [SmokeFact]
    public void DesktopFiveSeconds_TwoFakeTracks_ProducesOneVideoTwoAudio()
    {
        var ffmpeg = FfmpegLocator.Resolve("ffmpeg");
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
        if (!File.Exists(ffprobe)) ffprobe = FfmpegLocator.Resolve("ffprobe");

        var outDir = Path.Combine(Path.GetTempPath(), "peiwan smoke " + DateTime.Now.ToString("HHmmss"));
        var rec = new WindowsRecorder(
            new RecorderOptions { FfmpegPath = ffmpeg, OutputDirectory = outDir, Target = CaptureTarget.Desktop },
            FfmpegRunner.Create(ffmpeg), new FfmpegToneTrack(ffmpeg, 440), new FfmpegToneTrack(ffmpeg, 880));

        rec.Start(true, "smoke-session");
        Thread.Sleep(5000);
        var seg = rec.Stop()!;

        var sidecar = SegmentSidecar.FromJson(File.ReadAllText(Path.ChangeExtension(seg.FilePath, ".json")))!;
        Console.WriteLine($"mp4={seg.FilePath}\n{sidecar.ToJson()}");

        var probe = new FfmpegRunner(ffprobe).Run(["-v", "error", "-show_entries", "stream=codec_type,codec_name:stream_tags",
            "-of", "json", seg.FilePath], TimeSpan.FromSeconds(20));
        Assert.True(probe.Success, probe.Stderr);
        var streams = JsonDocument.Parse(probe.Stdout).RootElement.GetProperty("streams").EnumerateArray().ToList();
        Console.WriteLine(probe.Stdout);

        Assert.StartsWith("video_", sidecar.CodecMode);
        Assert.Equal(1, streams.Count(s => s.GetProperty("codec_type").GetString() == "video"));
        var audio = streams.Where(s => s.GetProperty("codec_type").GetString() == "audio").ToList();
        Assert.Equal(2, audio.Count);
        Assert.Equal("player_mic", Title(audio[0]));
        Assert.Equal("system_audio", Title(audio[1]));
        Assert.Equal("aac", audio[0].GetProperty("codec_name").GetString());
    }
}

