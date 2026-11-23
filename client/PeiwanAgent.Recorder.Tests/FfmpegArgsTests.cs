using PeiwanAgent.Recorder;
using PeiwanAgent.Recorder.Ffmpeg;

namespace PeiwanAgent.Recorder.Tests;

public class FfmpegArgsTests
{
    private static RecorderOptions Opts(CaptureTarget? t = null) => new()
    {
        OutputDirectory = @"C:\out dir", Target = t ?? CaptureTarget.ForProcess("Game.exe"),
    };

    [Fact]
    public void QuoteArgument_PlainIsUnchanged() => Assert.Equal("abc", FfmpegArgs.QuoteArgument("abc"));

    [Theory]
    [InlineData("", "\"\"")]
    [InlineData(@"C:\my dir\a b.mp4", "\"C:\\my dir\\a b.mp4\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
    [InlineData(@"a\""b c", "\"a\\\\\\\"b c\"")]
    public void QuoteArgument_FollowsWindowsRules(string input, string expected)
        => Assert.Equal(expected, FfmpegArgs.QuoteArgument(input));

    [Fact]
    public void ToCommandLine_JoinsAndQuotes()
        => Assert.Equal("-i \"a b.wav\" out.mp4", FfmpegArgs.ToCommandLine(["-i", "a b.wav", "out.mp4"]));

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a:b", @"a\\:b")]
    [InlineData("a,b", @"a\,b")]
    [InlineData("a;b[c]", @"a\;b\[c\]")]
    [InlineData("it's", @"it\\\'s")]
    [InlineData(@"a\b", @"a\\\\b")]
    public void EscapeFilterValue_TwoLevels(string input, string expected)
        => Assert.Equal(expected, FfmpegArgs.EscapeFilterValue(input));

    [Fact]
    public void ExeRegex_EscapesDotAndAnchors()
        => Assert.Equal(@"(?i)^Game\.exe$", FfmpegArgs.ExeRegex("Game.exe"));

    [Fact]
    public void ExeRegex_AppendsExe()
        => Assert.Equal(@"(?i)^Game\.exe$", FfmpegArgs.ExeRegex(" Game "));

    [Fact]
    public void GfxCapture_Desktop()
        => Assert.Equal("gfxcapture=monitor_idx=0:max_framerate=15:capture_cursor=true:resize_mode=scale_aspect",
            FfmpegArgs.GfxCaptureFilter(CaptureTarget.Desktop, 15));

    [Fact]
    public void GfxCapture_Hwnd()
        => Assert.Contains("hwnd=12345:", FfmpegArgs.GfxCaptureFilter(CaptureTarget.ForWindow(12345), 15));

    [Fact]
    public void GfxCapture_Process_UsesEscapedRegex()
        => Assert.Contains(@"window_exe=(?i)^Game\\\\.exe$:", FfmpegArgs.GfxCaptureFilter(CaptureTarget.ForProcess("Game.exe"), 15));

    [Fact]
    public void GfxCapture_Title_EscapesColonAndComma()
        => Assert.Contains(@"window_title=a\\:b\,c:", FfmpegArgs.GfxCaptureFilter(CaptureTarget.ForWindowTitle("a:b,c"), 15));

    [Fact]
    public void GfxCapture_EmptyProcessName_Throws()
        => Assert.Throws<ArgumentException>(() => FfmpegArgs.GfxCaptureFilter(CaptureTarget.ForProcess(" "), 15));

    [Fact]
    public void VideoCapture_HasNoAudio_720p_15fps_AndPathWithSpacesIsSingleArg()
    {
        var path = @"C:\out dir\tmp\video.mkv";
        var a = FfmpegArgs.VideoCaptureArgs(Opts(), "libx264", path);
        Assert.Equal(path, a[^1]);
        Assert.Contains("-an", a);
        var graph = a[a.ToList().IndexOf("-filter_complex") + 1];
        Assert.Contains("fps=15", graph);
        Assert.Contains(@"scale=-2:min(720\,ih)", graph);
        Assert.EndsWith("format=yuv420p[v]", graph);
        Assert.Contains("ultrafast", a);
    }

    [Theory]
    [InlineData("h264_nvenc", "p1")]
    [InlineData("h264_amf", "speed")]
    [InlineData("h264_qsv", "veryfast")]
    [InlineData("libx264", "ultrafast")]
    public void EncoderArgs_PerEncoder(string enc, string marker)
    {
        var a = FfmpegArgs.EncoderArgs(enc, 800, 15);
        Assert.Equal(enc, a[a.ToList().IndexOf("-c:v") + 1]);
        Assert.Contains(marker, a);
        Assert.Equal("800k", a[a.ToList().IndexOf("-b:v") + 1]);
        Assert.Equal("30", a[a.ToList().IndexOf("-g") + 1]);
    }

    [Fact]
    public void EncoderProbe_TargetsEncoderAndNullMuxer()
    {
        var a = FfmpegArgs.EncoderProbeArgs("h264_amf");
        Assert.Contains("h264_amf", a);
        Assert.Equal("null", a[^2]);
    }

    [Fact]
    public void Screenshots_Wgc_And_GdiGrab()
    {
        var o = Opts(); o.ScreenshotIntervalSeconds = 3;
        var dir = @"C:\out dir\seg.shots";
        var wgc = FfmpegArgs.ScreenshotArgs(o, false, dir);
        Assert.Equal(Path.Combine(dir, "shot_%05d.jpg"), wgc[^1]);
        Assert.Contains("fps=1/3", wgc[wgc.ToList().IndexOf("-filter_complex") + 1]);
        var gdi = FfmpegArgs.ScreenshotArgs(o, true, dir);
        Assert.Contains("gdigrab", gdi);
        Assert.DoesNotContain("-filter_complex", gdi);
    }

    [Fact]
    public void Mux_VideoAndTwoWavs_TitlesAndMapping()
    {
        var plan = new FfmpegArgs.MuxPlan(@"C:\a b\v.mkv", @"C:\a b\mic.wav", @"C:\a b\sys.wav", @"C:\out dir\seg.mp4");
        var a = FfmpegArgs.MuxArgs(plan);
        Assert.Equal(new[] { @"C:\a b\v.mkv", @"C:\a b\mic.wav", @"C:\a b\sys.wav" },
            a.Select((x, i) => (x, i)).Where(t => t.i > 0 && a[t.i - 1] == "-i").Select(t => t.x));
        Assert.Equal(@"C:\out dir\seg.mp4", a[^1]);
        Assert.Contains("title=player_mic", a);
        Assert.Contains("title=system_audio", a);
        Assert.Contains("handler_name=player_mic", a);
        Assert.Contains("handler_name=system_audio", a);
        Assert.Equal(["0:v:0", "1:a:0", "2:a:0"], a.Select((x, i) => (x, i)).Where(t => t.x == "-map").Select(t => a[t.i + 1]));
        Assert.Contains("aac", a);
        Assert.Contains("copy", a);
        Assert.True(a.ToList().IndexOf("title=player_mic") < a.ToList().IndexOf("title=system_audio"));
    }

    [Fact]
    public void Mux_MissingMic_UsesSilence_AudioOnly()
    {
        var a = FfmpegArgs.MuxArgs(new FfmpegArgs.MuxPlan(null, null, "s.wav", "o.mp4", 12.5));
        Assert.Contains("anullsrc=r=48000:cl=mono", a);
        Assert.Contains("12.5", a);
        Assert.DoesNotContain("-c:v", a);
        Assert.Equal(["0:a:0", "1:a:0"], a.Select((x, i) => (x, i)).Where(t => t.x == "-map").Select(t => a[t.i + 1]));
    }

    [Theory]
    [InlineData("  Duration: 00:01:02.50, start: 0.000000, bitrate: 64 kb/s", 62.5)]
    [InlineData("Duration: 01:00:00.00, x", 3600.0)]
    public void ParseDuration_Parses(string text, double expected)
        => Assert.Equal(expected, FfmpegArgs.ParseDuration(text)!.Value, 3);

    [Fact]
    public void ParseDuration_NA_ReturnsNull() => Assert.Null(FfmpegArgs.ParseDuration("Duration: N/A, start: 0"));
}

public class FfmpegLocatorTests
{
    [Fact]
    public void ExplicitPathMissing_ThrowsClearError()
    {
        var ex = Assert.Throws<FfmpegNotFoundException>(() => FfmpegLocator.Resolve(@"D:\nope\ffmpeg.exe", "", _ => false));
        Assert.Contains(@"D:\nope\ffmpeg.exe", ex.Message);
        Assert.Contains("ffmpeg", ex.Message);
    }

    [Fact]
    public void BareName_SearchesPath()
    {
        var r = FfmpegLocator.Resolve("ffmpeg", @"C:\a;C:\b", p => p == @"C:\b\ffmpeg.exe");
        Assert.Equal(@"C:\b\ffmpeg.exe", r);
    }

    [Fact]
    public void BareName_NotOnPath_Throws()
        => Assert.Throws<FfmpegNotFoundException>(() => FfmpegLocator.Resolve("ffmpeg", @"C:\a", _ => false));

    [Fact]
    public void ExplicitPathWithSpaces_Accepted()
        => Assert.Equal(@"C:\Program Files\ff mpeg\ffmpeg.exe",
            FfmpegLocator.Resolve("\"C:\\Program Files\\ff mpeg\\ffmpeg.exe\"", "", _ => true));
}


