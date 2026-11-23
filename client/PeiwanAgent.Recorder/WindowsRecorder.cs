using PeiwanAgent.Core.Recording;
using PeiwanAgent.Recorder.Ffmpeg;

namespace PeiwanAgent.Recorder;

/// <summary>
/// 每个切片三路并行:ffmpeg 视频(WGC) + 麦克风 WAV + 系统声 WAV,封存时封装为单个 mp4
/// (音轨 1 = player_mic,音轨 2 = system_audio)。仅使用 WGC/WASAPI 公开接口,不注入、不读游戏内存。
/// </summary>
public sealed class WindowsRecorder : IRecorder, IDisposable
{
    private readonly RecorderOptions _o;
    private readonly IFfmpegRunner _ffmpeg;
    private readonly ITrackCapture _mic;
    private readonly ITrackCapture _sys;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<string> _newId;
    private readonly object _gate = new();
    private IReadOnlyList<string>? _workingEncoders;
    private Active? _cur;

    public WindowsRecorder(RecorderOptions options, IFfmpegRunner ffmpeg, ITrackCapture mic, ITrackCapture system,
        Func<DateTimeOffset>? clock = null, Func<string>? segmentIdFactory = null)
    {
        _o = options;
        _ffmpeg = ffmpeg;
        _mic = mic;
        _sys = system;
        _now = clock ?? (() => DateTimeOffset.Now);
        _newId = segmentIdFactory ?? (() => Guid.NewGuid().ToString("N"));
        if (string.IsNullOrWhiteSpace(options.OutputDirectory))
            throw new ArgumentException("OutputDirectory 不能为空", nameof(options));
    }

    /// <summary>按配置解析 ffmpeg 路径(缺失时抛 <see cref="FfmpegNotFoundException"/>)并创建录制器。</summary>
    public static WindowsRecorder Create(RecorderOptions options, ITrackCapture mic, ITrackCapture system)
        => new(options, FfmpegRunner.Create(options.FfmpegPath), mic, system);

    private sealed class Active
    {
        public required string Id { get; init; }
        public string? SessionId { get; init; }
        public bool IsOrder { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public required string TmpDir { get; init; }
        public string MicWav => Path.Combine(TmpDir, "mic.wav");
        public string SysWav => Path.Combine(TmpDir, "system.wav");
        public string VideoFile => Path.Combine(TmpDir, "video.mkv");
        public string ShotsDir { get; init; } = "";
        public string Mode { get; set; } = CodecModes.AudioOnly;
        public string? Encoder { get; set; }
        public IManagedProcess? Video { get; set; }
        public bool MicOk, SysOk;
        public List<string> Warnings { get; } = [];
    }

    // ---------- IRecorder ----------

    public void Start(bool isOrder, string? sessionId)
    {
        lock (_gate)
        {
            if (_cur is not null) throw new InvalidOperationException("录制已在进行中,请使用 Split 或先 Stop。");
            _cur = Begin(isOrder, sessionId);
        }
    }

    public SegmentInfo Split(bool nextIsOrder, string? nextSessionId)
    {
        lock (_gate)
        {
            var old = _cur ?? throw new InvalidOperationException("尚未开始录制,无法 Split。");
            var endedAt = _now();
            RequestStop(old);                      // 先让旧切片的各路停下,再开新切片,缩小空档
            try { _cur = Begin(nextIsOrder, nextSessionId); }
            catch
            {
                _cur = null;
                try { Seal(old, endedAt); } catch { /* 保留原始异常 */ }
                throw;
            }
            return Seal(old, endedAt);
        }
    }

    public SegmentInfo? Stop()
    {
        lock (_gate)
        {
            var old = _cur;
            if (old is null) return null;
            _cur = null;
            var endedAt = _now();
            RequestStop(old);
            return Seal(old, endedAt);
        }
    }

    public void Dispose() { try { Stop(); } catch { } }

    // ---------- 开始 ----------

    private Active Begin(bool isOrder, string? sessionId)
    {
        Directory.CreateDirectory(_o.OutputDirectory);
        var id = _newId();
        var tmp = Path.Combine(_o.OutputDirectory, ".tmp", id);
        Directory.CreateDirectory(tmp);
        var seg = new Active
        {
            Id = id, SessionId = sessionId, IsOrder = isOrder, StartedAt = _now(), TmpDir = tmp,
            ShotsDir = Path.Combine(_o.OutputDirectory, id + ".shots"),
        };

        var encoders = WorkingEncoders();
        var pending = new Queue<string>(encoders.Take(2));
        string? encoder = pending.Count > 0 ? pending.Dequeue() : null;
        if (encoder is not null) seg.Video = SpawnVideo(seg, encoder);

        StartTrack(_mic, seg.MicWav, "麦克风", seg, ok => seg.MicOk = ok);
        StartTrack(_sys, seg.SysWav, "系统声", seg, ok => seg.SysOk = ok);

        // 确认视频进程没有立刻退出;失败则换下一个编码器,最后降级为截图
        if (seg.Video is not null) ConfirmVideoOrFallBack(seg, encoder!, pending);
        else if (encoder is null && !_o.AllowSoftwareEncode)
        {
            seg.Warnings.Add("无可用硬件编码器,按配置降级为 audio_screenshots");
            StartScreenshots(seg);
        }
        else StartScreenshots(seg);
        return seg;
    }

    private IReadOnlyList<string> WorkingEncoders()
    {
        if (_workingEncoders is not null) return _workingEncoders;
        var list = new List<string>();
        foreach (var hw in _o.HardwareEncoders)
        {
            FfmpegResult r;
            try { r = _ffmpeg.Run(FfmpegArgs.EncoderProbeArgs(hw), TimeSpan.FromSeconds(20)); }
            catch (FfmpegNotFoundException) { throw; }
            catch { continue; }
            if (r.Success) { list.Add(hw); break; } // 取第一个可用硬编
        }
        if (_o.AllowSoftwareEncode) list.Add(FfmpegArgs.SoftwareEncoder);
        return _workingEncoders = list;
    }

    private IManagedProcess SpawnVideo(Active seg, string encoder)
    {
        seg.Encoder = encoder;
        seg.Mode = encoder == FfmpegArgs.SoftwareEncoder ? CodecModes.VideoSoftware : CodecModes.VideoHardware;
        return _ffmpeg.Start(FfmpegArgs.VideoCaptureArgs(_o, encoder, seg.VideoFile));
    }

    private void ConfirmVideoOrFallBack(Active seg, string encoder, Queue<string> pending)
    {
        while (true)
        {
            if (WaitAlive(seg.Video!)) return;
            seg.Warnings.Add($"视频编码器 {encoder} 启动失败: {Tail(seg.Video!.Stderr)}");
            seg.Video.Dispose();
            seg.Video = null;
            if (pending.Count == 0) break;
            encoder = pending.Dequeue();
            seg.Video = SpawnVideo(seg, encoder);
        }
        seg.Encoder = null;
        StartScreenshots(seg);
    }

    private bool WaitAlive(IManagedProcess p)
    {
        if (_o.StartupCheckDelay > TimeSpan.Zero)
            return !p.WaitForExit(_o.StartupCheckDelay);
        return !p.HasExited;
    }

    private void StartScreenshots(Active seg)
    {
        seg.Mode = CodecModes.AudioScreenshots;
        seg.Encoder = null;
        Directory.CreateDirectory(seg.ShotsDir);
        var modes = _o.AllowDesktopScreenshotFallback ? new[] { false, true } : new[] { false };
        foreach (var gdi in modes)
        {
            var p = _ffmpeg.Start(FfmpegArgs.ScreenshotArgs(_o, gdi, seg.ShotsDir));
            if (WaitAlive(p)) { seg.Video = p; return; }
            seg.Warnings.Add($"截图进程({(gdi ? "gdigrab 桌面" : "WGC")})启动失败: {Tail(p.Stderr)}");
            p.Dispose();
        }
        seg.Mode = CodecModes.AudioOnly;
        seg.Warnings.Add("无法获取任何画面,仅录制音频");
        try { Directory.Delete(seg.ShotsDir, true); } catch { }
    }

    private static void StartTrack(ITrackCapture t, string path, string label, Active seg, Action<bool> setOk)
    {
        try { t.Start(path); setOk(true); }
        catch (Exception ex)
        {
            setOk(false);
            seg.Warnings.Add($"{label}采集启动失败,以静音占位: {ex.Message}");
        }
    }

    // ---------- 停止与封存 ----------

    private void RequestStop(Active seg)
    {
        seg.Video?.WriteStdin("q");
        if (seg.MicOk) StopTrack(_mic, "麦克风", seg);
        if (seg.SysOk) StopTrack(_sys, "系统声", seg);
    }

    private static void StopTrack(ITrackCapture t, string label, Active seg)
    {
        try { t.Stop(); }
        catch (Exception ex) { seg.Warnings.Add($"{label}停止时出错: {ex.Message}"); }
    }

    private SegmentInfo Seal(Active seg, DateTimeOffset endedAt)
    {
        if (seg.Video is { } v)
        {
            if (!v.WaitForExit(_o.GracefulStopTimeout))
            {
                seg.Warnings.Add("ffmpeg 未在超时内优雅退出,已强制结束");
                v.Kill();
                v.WaitForExit(TimeSpan.FromSeconds(3));
            }
            else if (v.ExitCode is not 0 and not null)
                seg.Warnings.Add($"ffmpeg 退出码 {v.ExitCode}: {Tail(v.Stderr)}");
            v.Dispose();
            seg.Video = null;
        }

        string? video = null;
        int shots = 0;
        if (seg.Mode is CodecModes.VideoHardware or CodecModes.VideoSoftware)
        {
            if (File.Exists(seg.VideoFile) && new FileInfo(seg.VideoFile).Length > 0) video = seg.VideoFile;
            else
            {
                seg.Warnings.Add("视频文件缺失或为空,本切片仅含音频");
                seg.Mode = CodecModes.AudioOnly;
            }
        }
        else if (seg.Mode == CodecModes.AudioScreenshots)
        {
            shots = Directory.Exists(seg.ShotsDir) ? Directory.GetFiles(seg.ShotsDir, "*.jpg").Length : 0;
            if (shots == 0) seg.Warnings.Add("降级模式未产生任何截图");
        }

        string? mic = UsableWav(seg.MicOk, seg.MicWav);
        string? sys = UsableWav(seg.SysOk, seg.SysWav);
        if (seg.MicOk && mic is null) seg.Warnings.Add("麦克风 WAV 为空,以静音占位");
        if (seg.SysOk && sys is null) seg.Warnings.Add("系统声 WAV 为空,以静音占位");

        double? vDur = video is null ? null : Duration(video);
        double? micDur = mic is null ? null : Duration(mic);
        double? sysDur = sys is null ? null : Duration(sys);
        double wall = Math.Max(0, (endedAt - seg.StartedAt).TotalSeconds);
        var known = new[] { vDur, micDur, sysDur }.Where(d => d.HasValue).Select(d => d!.Value).ToList();
        double silence = known.Count > 0 ? known.Max() : wall;

        var finalPath = Path.Combine(_o.OutputDirectory, seg.Id + ".mp4");
        var sidecarPath = Path.Combine(_o.OutputDirectory, seg.Id + ".json");

        // 时长对齐校验(只比较真实存在的轨)
        if (known.Count >= 2 && known.Max() - known.Min() > _o.DurationToleranceSeconds)
        {
            seg.Warnings.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"时长未对齐: video={Fmt(vDur)} mic={Fmt(micDur)} system={Fmt(sysDur)} (容差 {_o.DurationToleranceSeconds}s)"));
        }

        var r = _ffmpeg.Run(FfmpegArgs.MuxArgs(new FfmpegArgs.MuxPlan(video, mic, sys, finalPath, silence)), _o.MuxTimeout);
        if (!r.Success || !File.Exists(finalPath))
        {
            seg.Warnings.Add($"封装 mp4 失败(exit {r.ExitCode}{(r.TimedOut ? ", 超时" : "")}): {Tail(r.Stderr)}");
            WriteSidecar(seg, endedAt, null, vDur, micDur, sysDur, shots, sidecarPath);
            throw new RecorderException($"切片 {seg.Id} 封装失败,原始文件保留在 {seg.TmpDir}。{Tail(r.Stderr)}");
        }

        WriteSidecar(seg, endedAt, Path.GetFileName(finalPath), vDur, micDur, sysDur, shots, sidecarPath);
        try { Directory.Delete(seg.TmpDir, true); } catch { }
        return new SegmentInfo(seg.Id, seg.SessionId, seg.IsOrder, finalPath, seg.StartedAt, endedAt);
    }

    private static string? UsableWav(bool started, string path)
        => started && File.Exists(path) && new FileInfo(path).Length > 44 ? path : null;

    private double? Duration(string path)
    {
        try
        {
            var r = _ffmpeg.Run(FfmpegArgs.ProbeDurationArgs(path), TimeSpan.FromSeconds(20));
            return FfmpegArgs.ParseDuration(r.Stderr + r.Stdout);
        }
        catch (FfmpegNotFoundException) { throw; }
        catch { return null; }
    }

    private void WriteSidecar(Active seg, DateTimeOffset endedAt, string? file, double? v, double? m, double? s,
        int shots, string path)
    {
        var sc = new SegmentSidecar
        {
            SegmentId = seg.Id, SessionId = seg.SessionId, IsOrder = seg.IsOrder,
            StartedAt = seg.StartedAt, EndedAt = endedAt,
            CodecMode = seg.Mode, VideoEncoder = seg.Encoder, File = file,
            VideoDurationSeconds = v,
            Tracks =
            [
                new(1, FfmpegArgs.TrackMicTitle, m is null ? "silence_placeholder" : "wav", m),
                new(2, FfmpegArgs.TrackSystemTitle, s is null ? "silence_placeholder" : "wav", s),
            ],
            ScreenshotsDir = seg.Mode == CodecModes.AudioScreenshots ? Path.GetFileName(seg.ShotsDir) : null,
            ScreenshotCount = seg.Mode == CodecModes.AudioScreenshots ? shots : null,
            ScreenshotIntervalSeconds = seg.Mode == CodecModes.AudioScreenshots ? _o.ScreenshotIntervalSeconds : null,
            Warnings = seg.Warnings,
        };
        File.WriteAllText(path, sc.ToJson(), new System.Text.UTF8Encoding(false));
    }

    private static string Fmt(double? d) => d?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a";

    private static string Tail(string s)
    {
        s = s.Trim();
        return s.Length <= 400 ? s : "…" + s[^400..];
    }
}
