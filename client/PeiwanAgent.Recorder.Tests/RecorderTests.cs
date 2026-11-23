using PeiwanAgent.Core.Recording;
using PeiwanAgent.Recorder.Ffmpeg;

namespace PeiwanAgent.Recorder.Tests;

internal sealed class FakeTrack : ITrackCapture
{
    public bool FailStart;
    public bool WriteEmpty;
    public int Starts, Stops;
    public string? LastPath;
    public void Start(string wavPath)
    {
        Starts++;
        if (FailStart) throw new InvalidOperationException("no device");
        LastPath = wavPath;
        File.WriteAllBytes(wavPath, new byte[WriteEmpty ? 44 : 4096]);
    }
    public void Stop() => Stops++;
    public void Dispose() { }
}

internal sealed class FakeProcess : IManagedProcess
{
    public bool HasExited { get; set; }
    public int? ExitCode { get; set; }
    public string Stdout { get; set; } = "";
    public string Stderr { get; set; } = "";
    public List<string> Stdin { get; } = [];
    public bool Killed;
    public bool IgnoreQuit;
    public void WriteStdin(string text)
    {
        Stdin.Add(text);
        if (!IgnoreQuit && text == "q") { HasExited = true; ExitCode ??= 0; }
    }
    public bool WaitForExit(TimeSpan timeout) => HasExited;
    public void Kill() { Killed = true; HasExited = true; ExitCode ??= -1; }
    public void Dispose() { }
}

/// <summary>脚本化的 ffmpeg:按参数特征模拟探测、录制、封装。</summary>
internal sealed class FakeFfmpeg : IFfmpegRunner
{
    public string FfmpegPath => "ffmpeg-fake";
    public HashSet<string> WorkingHw = [];
    public bool WgcFails;                 // gfxcapture 进程立刻退出
    public bool GdiFails;
    public bool MuxFails;
    public bool VideoIgnoresQuit;
    public Dictionary<string, double> Durations = new();   // 文件名片段 -> 秒
    public List<IReadOnlyList<string>> Muxes { get; } = [];
    public List<IReadOnlyList<string>> Started { get; } = [];
    public List<FakeProcess> Procs { get; } = [];

    public FfmpegResult Run(IReadOnlyList<string> args, TimeSpan timeout)
    {
        if (args.Contains("-f") && args.Contains("null") && args.Contains("lavfi"))
        {
            var enc = args[args.ToList().IndexOf("-c:v") + 1];
            return new FfmpegResult(WorkingHw.Contains(enc) ? 0 : 1, "", "probe");
        }
        if (args.Count == 4 && args[^2] == "-i") // 时长探测
        {
            var f = args[^1];
            foreach (var kv in Durations)
                if (f.Contains(kv.Key))
                    return new FfmpegResult(1, "", $"  Duration: 00:00:{kv.Value:00.00}, start: 0");
            return new FfmpegResult(1, "", "  Duration: 00:00:10.00, start: 0");
        }
        Muxes.Add(args);
        if (MuxFails) return new FfmpegResult(1, "", "mux boom");
        File.WriteAllBytes(args[^1], [1, 2, 3]);
        return new FfmpegResult(0, "", "");
    }

    public IManagedProcess Start(IReadOnlyList<string> args)
    {
        Started.Add(args);
        var p = new FakeProcess { IgnoreQuit = VideoIgnoresQuit };
        bool shots = args.Contains("image2");
        bool gdi = args.Contains("gdigrab");
        bool fail = shots ? (gdi ? GdiFails : WgcFails) : WgcFails;
        if (fail) { p.HasExited = true; p.ExitCode = 1; p.Stderr = "capture failed"; }
        else if (shots) File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(args[^1])!, "shot_00001.jpg"), [1]);
        else File.WriteAllBytes(args[^1], [1, 2, 3]);
        Procs.Add(p);
        return p;
    }
}

public sealed class WindowsRecorderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "peiwan rec test " + Guid.NewGuid().ToString("N"));
    private readonly FakeFfmpeg _ff = new();
    private readonly FakeTrack _mic = new();
    private readonly FakeTrack _sys = new();
    private int _n;
    private DateTimeOffset _t = new(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public WindowsRecorderTests() { _ff.WorkingHw.Add("h264_nvenc"); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private WindowsRecorder Make() => new(
        new RecorderOptions { OutputDirectory = _dir, StartupCheckDelay = TimeSpan.Zero },
        _ff, _mic, _sys,
        () => _t = _t.AddSeconds(5), () => "seg" + (++_n));

    private SegmentSidecar Sidecar(SegmentInfo s)
        => SegmentSidecar.FromJson(File.ReadAllText(Path.ChangeExtension(s.FilePath, ".json")))!;

    [Fact]
    public void HardwareHappyPath_OneSegment_FileNamedBySegmentId_SidecarComplete()
    {
        var r = Make();
        r.Start(true, "sess-1");
        var seg = r.Stop()!;

        Assert.Equal("seg1", seg.SegmentId);
        Assert.Equal(Path.Combine(_dir, "seg1.mp4"), seg.FilePath);
        Assert.True(File.Exists(seg.FilePath));
        Assert.True(seg.IsOrder);
        Assert.Equal("sess-1", seg.SessionId);
        Assert.True(seg.EndedAt > seg.StartedAt);

        var sc = Sidecar(seg);
        Assert.Equal("video_hw", sc.CodecMode);
        Assert.Equal("h264_nvenc", sc.VideoEncoder);
        Assert.Equal("sess-1", sc.SessionId);
        Assert.True(sc.IsOrder);
        Assert.Equal(seg.StartedAt, sc.StartedAt);
        Assert.Equal(seg.EndedAt, sc.EndedAt);
        Assert.Empty(sc.Warnings);
        Assert.Equal(["player_mic", "system_audio"], sc.Tracks.Select(t => t.Title));
        Assert.Equal(1, _mic.Stops);
        Assert.Equal(1, _sys.Stops);
        Assert.Contains("q", _ff.Procs[0].Stdin);          // 优雅退出
        Assert.False(_ff.Procs[0].Killed);
        Assert.False(Directory.Exists(Path.Combine(_dir, ".tmp", "seg1")));
        Assert.Contains("h264_nvenc", _ff.Started[0]);
    }

    [Fact]
    public void NoHardware_FallsBackToLibx264()
    {
        _ff.WorkingHw.Clear();
        var r = Make(); r.Start(false, null);
        var seg = r.Stop()!;
        var sc = Sidecar(seg);
        Assert.Equal("video_sw", sc.CodecMode);
        Assert.Equal("libx264", sc.VideoEncoder);
        Assert.False(sc.IsOrder);
        Assert.Null(sc.SessionId);
    }

    [Fact]
    public void HardwareEncoderFailsToStart_TriesLibx264Next()
    {
        // nvenc 探测通过但真实启动失败:这里用 X264 正常、nvenc 进程失败模拟
        var ff = new FakeFfmpeg { WorkingHw = { "h264_nvenc" } };
        var r = new WindowsRecorder(new RecorderOptions { OutputDirectory = _dir, StartupCheckDelay = TimeSpan.Zero },
            new FailFirstVideo(ff), _mic, _sys, null, () => "x1");
        r.Start(true, "s");
        var sc = Sidecar(r.Stop()!);
        Assert.Equal("video_sw", sc.CodecMode);
        Assert.Contains(sc.Warnings, w => w.Contains("h264_nvenc"));
    }

    private sealed class FailFirstVideo(FakeFfmpeg inner) : IFfmpegRunner
    {
        private bool _done;
        public string FfmpegPath => inner.FfmpegPath;
        public FfmpegResult Run(IReadOnlyList<string> a, TimeSpan t) => inner.Run(a, t);
        public IManagedProcess Start(IReadOnlyList<string> a)
        {
            if (!_done && a.Contains("h264_nvenc")) { _done = true; return new FakeProcess { HasExited = true, ExitCode = 1, Stderr = "nvenc died" }; }
            return inner.Start(a);
        }
    }

    [Fact]
    public void WgcUnavailable_DegradesToScreenshots_AudioStillRecorded()
    {
        _ff.WgcFails = true;
        var r = Make(); r.Start(true, "s");
        var seg = r.Stop()!;
        var sc = Sidecar(seg);
        Assert.Equal("audio_screenshots", sc.CodecMode);
        Assert.Null(sc.VideoEncoder);
        Assert.Equal(1, sc.ScreenshotCount);
        Assert.Equal("seg1.shots", sc.ScreenshotsDir);
        Assert.Equal(3, sc.ScreenshotIntervalSeconds);
        Assert.NotEmpty(sc.Warnings);
        Assert.Equal(1, _mic.Starts);
        Assert.Equal(1, _sys.Starts);
        // 音频 + 静态截图,mux 无视频输入
        Assert.DoesNotContain("-c:v", _ff.Muxes[0]);
        Assert.True(File.Exists(seg.FilePath));
    }

    [Fact]
    public void WgcUnavailable_NoSoftwareAllowed_NoHardware_GoesToScreenshotsDirectly()
    {
        _ff.WorkingHw.Clear();
        var o = new RecorderOptions { OutputDirectory = _dir, StartupCheckDelay = TimeSpan.Zero, AllowSoftwareEncode = false };
        var r = new WindowsRecorder(o, _ff, _mic, _sys, null, () => "n1");
        r.Start(true, null);
        var sc = Sidecar(r.Stop()!);
        Assert.Equal("audio_screenshots", sc.CodecMode);
        Assert.Contains(sc.Warnings, w => w.Contains("硬件编码器"));
    }

    [Fact]
    public void WgcAndGdiBothFail_AudioOnly()
    {
        _ff.WgcFails = true; _ff.GdiFails = true;
        var r = Make(); r.Start(true, null);
        var sc = Sidecar(r.Stop()!);
        Assert.Equal("audio_only", sc.CodecMode);
        Assert.False(Directory.Exists(Path.Combine(_dir, "seg1.shots")));
    }

    [Fact]
    public void DesktopFallbackDisabled_DoesNotLaunchGdiGrab()
    {
        _ff.WgcFails = true;
        var o = new RecorderOptions { OutputDirectory = _dir, StartupCheckDelay = TimeSpan.Zero, AllowDesktopScreenshotFallback = false };
        var r = new WindowsRecorder(o, _ff, _mic, _sys, null, () => "d1");
        r.Start(true, null);
        var sc = Sidecar(r.Stop()!);
        Assert.Equal("audio_only", sc.CodecMode);
        Assert.DoesNotContain(_ff.Started, a => a.Contains("gdigrab"));
    }

    [Fact]
    public void Split_SealsOldAndStartsNew_ParametersSeparated()
    {
        var r = Make();
        r.Start(false, null);
        var first = r.Split(true, "order-9");
        Assert.False(first.IsOrder);
        Assert.Null(first.SessionId);
        Assert.Equal("seg1", first.SegmentId);
        Assert.True(File.Exists(first.FilePath));

        var second = r.Stop()!;
        Assert.Equal("seg2", second.SegmentId);
        Assert.True(second.IsOrder);
        Assert.Equal("order-9", second.SessionId);
        Assert.NotEqual(first.FilePath, second.FilePath);
        Assert.Equal(2, _mic.Starts);
        Assert.Equal(2, _mic.Stops);
        Assert.Contains("seg2", _mic.LastPath);
        Assert.Equal(first.EndedAt, Sidecar(first).EndedAt);
    }

    [Fact]
    public void Stop_WhenNotStarted_ReturnsNull() => Assert.Null(Make().Stop());

    [Fact]
    public void Split_WhenNotStarted_Throws() => Assert.Throws<InvalidOperationException>(() => Make().Split(true, "x"));

    [Fact]
    public void Start_Twice_Throws()
    {
        var r = Make(); r.Start(true, null);
        Assert.Throws<InvalidOperationException>(() => r.Start(true, null));
        r.Stop();
    }

    [Fact]
    public void MicStartFails_SilencePlaceholder_Warned_StillTwoTracks()
    {
        _mic.FailStart = true;
        var r = Make(); r.Start(true, null);
        var seg = r.Stop()!;
        var sc = Sidecar(seg);
        Assert.Contains(sc.Warnings, w => w.Contains("麦克风"));
        Assert.Equal("silence_placeholder", sc.Tracks[0].Source);
        Assert.Equal("wav", sc.Tracks[1].Source);
        Assert.Contains("anullsrc=r=48000:cl=mono", _ff.Muxes[0]);
        Assert.Equal(0, _mic.Stops); // 没启动成功就不 Stop
    }

    [Fact]
    public void EmptyWav_TreatedAsMissing()
    {
        _sys.WriteEmpty = true;
        var r = Make(); r.Start(true, null);
        var sc = Sidecar(r.Stop()!);
        Assert.Equal("silence_placeholder", sc.Tracks[1].Source);
        Assert.Contains(sc.Warnings, w => w.Contains("系统声"));
    }

    [Fact]
    public void DurationMismatch_WritesWarning()
    {
        _ff.Durations["video.mkv"] = 10; _ff.Durations["mic.wav"] = 10; _ff.Durations["system.wav"] = 4;
        var r = Make(); r.Start(true, null);
        var sc = Sidecar(r.Stop()!);
        Assert.Contains(sc.Warnings, w => w.Contains("时长未对齐"));
        Assert.Equal(10, sc.VideoDurationSeconds);
    }

    [Fact]
    public void DurationWithinTolerance_NoWarning()
    {
        _ff.Durations["video.mkv"] = 10; _ff.Durations["mic.wav"] = 9.5; _ff.Durations["system.wav"] = 10.4;
        var r = Make(); r.Start(true, null);
        Assert.Empty(Sidecar(r.Stop()!).Warnings);
    }

    [Fact]
    public void FfmpegIgnoresQuit_IsKilled_WithWarning()
    {
        _ff.VideoIgnoresQuit = true;
        var r = Make(); r.Start(true, null);
        var sc = Sidecar(r.Stop()!);
        Assert.True(_ff.Procs[0].Killed);
        Assert.Contains(sc.Warnings, w => w.Contains("强制"));
    }

    [Fact]
    public void MuxFails_ThrowsAndKeepsRawFiles_SidecarHasWarning()
    {
        _ff.MuxFails = true;
        var r = Make(); r.Start(true, null);
        var ex = Assert.Throws<RecorderException>(() => r.Stop());
        Assert.Contains("seg1", ex.Message);
        Assert.True(Directory.Exists(Path.Combine(_dir, ".tmp", "seg1")));
        var sc = SegmentSidecar.FromJson(File.ReadAllText(Path.Combine(_dir, "seg1.json")))!;
        Assert.Contains(sc.Warnings, w => w.Contains("封装"));
        Assert.Null(sc.File);
    }

    [Fact]
    public void MuxArgs_UsedWithPathsContainingSpaces()
    {
        var r = Make(); r.Start(true, null); r.Stop();
        var m = _ff.Muxes[0];
        Assert.Equal(Path.Combine(_dir, "seg1.mp4"), m[^1]);
        Assert.Contains(m, a => a.Contains("peiwan rec test") && a.EndsWith("mic.wav"));
    }

    [Fact]
    public void Constructor_RequiresOutputDirectory()
        => Assert.Throws<ArgumentException>(() => new WindowsRecorder(new RecorderOptions(), _ff, _mic, _sys));

    [Fact]
    public void Create_WithMissingFfmpeg_ThrowsClearError()
    {
        var o = new RecorderOptions { OutputDirectory = _dir, FfmpegPath = @"Z:\no such\ffmpeg.exe" };
        var ex = Assert.Throws<FfmpegNotFoundException>(() => WindowsRecorder.Create(o, _mic, _sys));
        Assert.Contains("Z:\\no such\\ffmpeg.exe", ex.Message);
    }
}

