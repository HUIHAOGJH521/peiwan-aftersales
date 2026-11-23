namespace PeiwanAgent.Recorder;

public enum CaptureTargetKind { Desktop, ProcessName, WindowTitle, WindowHandle }

/// <summary>WGC 抓取目标。仅通过 Windows.Graphics.Capture 公开接口抓取,不接触目标进程。</summary>
public sealed record CaptureTarget(CaptureTargetKind Kind, string? Value = null, long Handle = 0)
{
    public static CaptureTarget Desktop { get; } = new(CaptureTargetKind.Desktop);
    /// <summary>按可执行文件名匹配窗口,例如 "DeltaForceClient-Win64-Shipping.exe"。</summary>
    public static CaptureTarget ForProcess(string exeName) => new(CaptureTargetKind.ProcessName, exeName);
    /// <summary>按窗口标题(正则)匹配。</summary>
    public static CaptureTarget ForWindowTitle(string titleRegex) => new(CaptureTargetKind.WindowTitle, titleRegex);
    public static CaptureTarget ForWindow(long hwnd) => new(CaptureTargetKind.WindowHandle, null, hwnd);
}

public sealed class RecorderOptions
{
    /// <summary>ffmpeg 可执行文件路径;可为绝对路径或 PATH 中的文件名。</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";
    /// <summary>切片与 sidecar 输出目录。</summary>
    public string OutputDirectory { get; set; } = "";
    public CaptureTarget Target { get; set; } = CaptureTarget.Desktop;
    public int VideoHeight { get; set; } = 720;
    public int VideoFps { get; set; } = 15;
    public int VideoBitrateKbps { get; set; } = 800;
    /// <summary>硬编探测顺序。</summary>
    public string[] HardwareEncoders { get; set; } = ["h264_nvenc", "h264_amf", "h264_qsv"];
    /// <summary>无硬编时是否退到 libx264 ultrafast。为 false 时无硬编直接走 audio_screenshots 降级。</summary>
    public bool AllowSoftwareEncode { get; set; } = true;
    /// <summary>降级模式截图间隔(秒)。</summary>
    public int ScreenshotIntervalSeconds { get; set; } = 3;
    /// <summary>WGC 不可用时,截图是否允许退到 gdigrab 抓整个桌面。</summary>
    public bool AllowDesktopScreenshotFallback { get; set; } = true;
    /// <summary>视频/麦克风/系统声时长互差超过该值时在 sidecar 写警告。</summary>
    public double DurationToleranceSeconds { get; set; } = 2.0;
    /// <summary>启动 ffmpeg 后等待多久确认其未立即退出。</summary>
    public TimeSpan StartupCheckDelay { get; set; } = TimeSpan.FromMilliseconds(1500);
    /// <summary>向 ffmpeg 发送 q 后等待其退出的时间,超时强杀。</summary>
    public TimeSpan GracefulStopTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan MuxTimeout { get; set; } = TimeSpan.FromMinutes(3);
}
