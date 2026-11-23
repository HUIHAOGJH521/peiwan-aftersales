using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PeiwanAgent.Recorder.Ffmpeg;

/// <summary>ffmpeg 命令行构造(纯函数,无副作用)。返回参数列表,由 ProcessStartInfo.ArgumentList 传递,不经 shell。</summary>
public static class FfmpegArgs
{
    public const string SoftwareEncoder = "libx264";
    public const string TrackMicTitle = "player_mic";
    public const string TrackSystemTitle = "system_audio";

    // ---------- 转义 ----------

    /// <summary>
    /// 转义 filtergraph 中的选项值(两层:先选项层 \ : ',再图层 \ ' [ ] , ;)。
    /// 用于窗口标题等任意文本。
    /// </summary>
    public static string EscapeFilterValue(string value)
    {
        var l1 = new StringBuilder();
        foreach (var c in value)
        {
            if (c is '\\' or ':' or '\'') l1.Append('\\');
            l1.Append(c);
        }
        var l2 = new StringBuilder();
        foreach (var c in l1.ToString())
        {
            if (c is '\\' or '\'' or '[' or ']' or ',' or ';') l2.Append('\\');
            l2.Append(c);
        }
        return l2.ToString();
    }

    /// <summary>按 Windows CommandLineToArgvW 规则引用单个参数(仅用于日志/诊断展示)。</summary>
    public static string QuoteArgument(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return arg;
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') { sb.Append('\\', backslashes * 2 + 1).Append('"'); }
            else { sb.Append('\\', backslashes).Append(c); }
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    public static string ToCommandLine(IEnumerable<string> args) => string.Join(' ', args.Select(QuoteArgument));

    // ---------- 采集源 ----------

    /// <summary>WGC 采集滤镜(gfxcapture)。通过系统公开的 Windows.Graphics.Capture,不触碰目标进程。</summary>
    public static string GfxCaptureFilter(CaptureTarget target, int fps)
    {
        string sel = target.Kind switch
        {
            CaptureTargetKind.Desktop => "monitor_idx=0",
            CaptureTargetKind.ProcessName => "window_exe=" + EscapeFilterValue(ExeRegex(RequireValue(target))),
            CaptureTargetKind.WindowTitle => "window_title=" + EscapeFilterValue(RequireValue(target)),
            CaptureTargetKind.WindowHandle => "hwnd=" + target.Handle.ToString(CultureInfo.InvariantCulture),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
        return $"gfxcapture={sel}:max_framerate={fps}:capture_cursor=true:resize_mode=scale_aspect";
    }

    private static string RequireValue(CaptureTarget t)
        => string.IsNullOrWhiteSpace(t.Value) ? throw new ArgumentException("捕获目标缺少值", nameof(t)) : t.Value!;

    /// <summary>可执行文件名转为不区分大小写的完整匹配正则。</summary>
    public static string ExeRegex(string exeName)
    {
        var n = exeName.Trim();
        if (!n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n += ".exe";
        return "(?i)^" + Regex.Escape(n) + "$";
    }

    private static string ScaleFilter(int height) => $"scale=-2:min({height}\\,ih)";

    // ---------- 编码器 ----------

    public static IReadOnlyList<string> EncoderProbeArgs(string encoder) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostdin",
        "-f", "lavfi", "-i", "color=c=black:s=1280x720:r=15:d=1",
        "-frames:v", "5", "-pix_fmt", "yuv420p", "-c:v", encoder, "-f", "null", "-",
    ];

    public static IReadOnlyList<string> EncoderArgs(string encoder, int bitrateKbps, int fps)
    {
        var b = $"{bitrateKbps}k";
        var max = $"{bitrateKbps * 5 / 4}k";
        var buf = $"{bitrateKbps * 2}k";
        var gop = (fps * 2).ToString(CultureInfo.InvariantCulture);
        var common = new[] { "-b:v", b, "-maxrate", max, "-bufsize", buf, "-g", gop };
        string[] specific = encoder switch
        {
            "h264_nvenc" => ["-c:v", "h264_nvenc", "-preset", "p1", "-rc", "vbr"],
            "h264_amf" => ["-c:v", "h264_amf", "-usage", "transcoding", "-quality", "speed", "-rc", "vbr_peak"],
            "h264_qsv" => ["-c:v", "h264_qsv", "-preset", "veryfast"],
            SoftwareEncoder => ["-c:v", "libx264", "-preset", "ultrafast", "-threads", "2"],
            _ => ["-c:v", encoder],
        };
        return [.. specific, .. common];
    }

    // ---------- 录制 ----------

    /// <summary>仅视频、无音频的录制命令,输出 Matroska(异常中断也可读)。</summary>
    public static IReadOnlyList<string> VideoCaptureArgs(RecorderOptions o, string encoder, string outPath)
    {
        var graph = $"{GfxCaptureFilter(o.Target, o.VideoFps)},hwdownload,format=bgra,fps={o.VideoFps}," +
                    $"{ScaleFilter(o.VideoHeight)},format=yuv420p[v]";
        return
        [
            "-hide_banner", "-loglevel", "warning", "-nostats", "-y",
            "-filter_complex", graph, "-map", "[v]",
            .. EncoderArgs(encoder, o.VideoBitrateKbps, o.VideoFps),
            "-an", "-f", "matroska", outPath,
        ];
    }

    /// <summary>降级模式:每 N 秒一张 JPEG 截图。<paramref name="useGdiGrab"/> 为 true 时抓整个桌面。</summary>
    public static IReadOnlyList<string> ScreenshotArgs(RecorderOptions o, bool useGdiGrab, string outDir)
    {
        var every = o.ScreenshotIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        var pattern = Path.Combine(outDir, "shot_%05d.jpg");
        var head = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostats", "-y" };
        if (useGdiGrab)
        {
            head.AddRange(["-f", "gdigrab", "-framerate", "1", "-i", "desktop",
                "-vf", $"fps=1/{every},{ScaleFilter(o.VideoHeight)},format=yuvj420p", "-an"]);
        }
        else
        {
            var graph = $"{GfxCaptureFilter(o.Target, 1)},hwdownload,format=bgra,fps=1/{every}," +
                        $"{ScaleFilter(o.VideoHeight)},format=yuvj420p[v]";
            head.AddRange(["-filter_complex", graph, "-map", "[v]", "-an"]);
        }
        head.AddRange(["-q:v", "5", "-f", "image2", "-start_number", "1", pattern]);
        return head;
    }

    // ---------- 封装 ----------

    /// <summary>
    /// 封装计划。音轨顺序固定:音轨 1 = 麦克风(player_mic),音轨 2 = 系统声(system_audio)。
    /// WAV 缺失时用 <see cref="MuxPlan.SilenceSeconds"/> 秒的静音占位,保证两条音轨始终存在。
    /// </summary>
    public sealed record MuxPlan(string? VideoPath, string? MicWav, string? SystemWav, string OutputPath,
        double SilenceSeconds = 0);

    public static IReadOnlyList<string> MuxArgs(MuxPlan p)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" };
        int idx = 0;
        int? v = null;
        if (p.VideoPath is not null) { args.AddRange(["-i", p.VideoPath]); v = idx++; }
        int mic = AddAudioInput(args, p.MicWav, p.SilenceSeconds, ref idx);
        int sys = AddAudioInput(args, p.SystemWav, p.SilenceSeconds, ref idx);

        if (v is int vi) args.AddRange(["-map", $"{vi}:v:0"]);
        args.AddRange(["-map", $"{mic}:a:0", "-map", $"{sys}:a:0"]);
        if (v is not null) args.AddRange(["-c:v", "copy"]);
        args.AddRange(["-c:a", "aac", "-b:a", "64k",
            "-metadata:s:a:0", $"title={TrackMicTitle}", "-metadata:s:a:0", $"handler_name={TrackMicTitle}",
            "-metadata:s:a:1", $"title={TrackSystemTitle}", "-metadata:s:a:1", $"handler_name={TrackSystemTitle}",
            "-movflags", "+faststart", p.OutputPath]);
        return args;
    }

    private static int AddAudioInput(List<string> args, string? wav, double silenceSeconds, ref int idx)
    {
        if (wav is not null) args.AddRange(["-i", wav]);
        else
        {
            var d = Math.Max(0.1, silenceSeconds).ToString("0.###", CultureInfo.InvariantCulture);
            args.AddRange(["-f", "lavfi", "-t", d, "-i", "anullsrc=r=48000:cl=mono"]);
        }
        return idx++;
    }

    // ---------- 探测 ----------

    public static IReadOnlyList<string> ProbeDurationArgs(string path) =>
        ["-hide_banner", "-nostdin", "-i", path];

    /// <summary>从 `ffmpeg -i` 的 stderr 解析 Duration(秒)。N/A 或缺失返回 null。</summary>
    public static double? ParseDuration(string ffmpegOutput)
    {
        var m = Regex.Match(ffmpegOutput, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        if (!m.Success) return null;
        return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600.0
             + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60.0
             + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
    }
}

