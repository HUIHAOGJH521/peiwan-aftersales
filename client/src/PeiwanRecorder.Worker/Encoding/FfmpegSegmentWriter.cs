using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using PeiwanAgent.Core.Models;

namespace PeiwanRecorder.Worker.Encoding;

/// <summary>
/// 管理单个切片的 FFmpeg 编码子进程及命名管道写入（视频 rawvideo + 音轨1 mic + 音轨2 system）。
/// </summary>
public sealed class FfmpegSegmentWriter : IAsyncDisposable
{
    private readonly WorkerCommand _command;
    private readonly string _ffmpegPath;
    private readonly int _videoWidth;
    private readonly int _videoHeight;
    private readonly bool _isVideoHw;

    private readonly NamedPipeServerStream? _vidPipe;
    private readonly NamedPipeServerStream _micPipe;
    private readonly NamedPipeServerStream _sysPipe;
    private readonly Process _ffmpegProc;
    private readonly string _framesDir;
    private readonly object _writeLock = new();

    private volatile bool _closing;
    private int _screenshotSeq;
    private DateTimeOffset _lastScreenshotAt = DateTimeOffset.MinValue;
    public DateTimeOffset StartedAt { get; }

    private FfmpegSegmentWriter(
        WorkerCommand command,
        string ffmpegPath,
        int videoWidth,
        int videoHeight,
        bool isVideoHw,
        NamedPipeServerStream? vidPipe,
        NamedPipeServerStream micPipe,
        NamedPipeServerStream sysPipe,
        Process ffmpegProc,
        string framesDir,
        DateTimeOffset startedAt)
    {
        _command = command;
        _ffmpegPath = ffmpegPath;
        _videoWidth = videoWidth;
        _videoHeight = videoHeight;
        _isVideoHw = isVideoHw;
        _vidPipe = vidPipe;
        _micPipe = micPipe;
        _sysPipe = sysPipe;
        _ffmpegProc = ffmpegProc;
        _framesDir = framesDir;
        StartedAt = startedAt;
    }

    public static async Task<FfmpegSegmentWriter> StartAsync(
        WorkerCommand command,
        string ffmpegPath,
        int videoWidth,
        int videoHeight,
        CancellationToken ct = default)
    {
        var outputPath = command.OutputPath ?? throw new ArgumentException("OutputPath required");
        var outDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        var isVideoHw = string.Equals(command.CodecMode, "video_hw", StringComparison.OrdinalIgnoreCase);
        var pipeTag = Guid.NewGuid().ToString("N")[..12];
        var vidPipeName = $"peiwan_vid_{pipeTag}";
        var micPipeName = $"peiwan_mic_{pipeTag}";
        var sysPipeName = $"peiwan_sys_{pipeTag}";

        NamedPipeServerStream? vidPipe = isVideoHw
            ? new NamedPipeServerStream(vidPipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4 * 1024 * 1024, 4 * 1024 * 1024)
            : null;
        var micPipe = new NamedPipeServerStream(micPipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 256 * 1024, 256 * 1024);
        var sysPipe = new NamedPipeServerStream(sysPipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 512 * 1024, 512 * 1024);

        var framesDir = Path.Combine(outDir ?? ".", $"{command.SegmentId}_frames");
        if (!isVideoHw)
        {
            Directory.CreateDirectory(framesDir);
        }

        var args = BuildFfmpegArgs(command, isVideoHw, videoWidth, videoHeight, vidPipeName, micPipeName, sysPipeName, outputPath);
        var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        if (!proc.Start())
        {
            vidPipe?.Dispose();
            micPipe.Dispose();
            sysPipe.Dispose();
            throw new InvalidOperationException("Failed to start ffmpeg process");
        }

        var stderrLines = new List<string>();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!proc.StandardError.EndOfStream)
                {
                    var line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        lock (stderrLines)
                        {
                            if (stderrLines.Count < 20) stderrLines.Add(line);
                        }
                    }
                }
            }
            catch { }
        });

        // FFmpeg 按输入顺序逐个打开命名管道并探测首包，必须并发非阻塞等待连接，否则 Input 0 等待数据而 Input 1/2 尚未打开会死锁
        if (vidPipe != null)
        {
            _ = vidPipe.WaitForConnectionAsync();
        }
        _ = micPipe.WaitForConnectionAsync();
        _ = sysPipe.WaitForConnectionAsync();

        // 短暂让出一次调度确保管道监听已就绪
        await Task.Delay(30, ct).ConfigureAwait(false);

        return new FfmpegSegmentWriter(
            command,
            ffmpegPath,
            videoWidth,
            videoHeight,
            isVideoHw,
            vidPipe,
            micPipe,
            sysPipe,
            proc,
            framesDir,
            DateTimeOffset.UtcNow);
    }

    private static string BuildFfmpegArgs(
        WorkerCommand command,
        bool isVideoHw,
        int width,
        int height,
        string vidPipeName,
        string micPipeName,
        string sysPipeName,
        string outputPath)
    {
        var fps = Math.Clamp(command.Fps, 5, 30);
        var bitrate = Math.Clamp(command.BitrateKbps, 300, 4000);

        if (isVideoHw)
        {
            var encoder = string.IsNullOrWhiteSpace(command.HwEncoder) ? "libx264" : command.HwEncoder;
            var presetArg = encoder switch
            {
                "h264_nvenc" => "-preset p4 -tune ll",
                "h264_amf" => "-quality speed",
                "h264_qsv" => "-preset veryfast",
                _ => "-preset ultrafast"
            };

            return
                $"-y -hide_banner -loglevel error " +
                $"-f rawvideo -pix_fmt bgra -s {width}x{height} -r {fps} -i \\\\.\\pipe\\{vidPipeName} " +
                $"-f s16le -ar 48000 -ac 1 -i \\\\.\\pipe\\{micPipeName} " +
                $"-f s16le -ar 48000 -ac 2 -i \\\\.\\pipe\\{sysPipeName} " +
                $"-map 0:v -map 1:a -map 2:a " +
                $"-vf \"scale=1280:-2,format=yuv420p\" " +
                $"-c:v {encoder} {presetArg} -b:v {bitrate}k -maxrate {bitrate * 2}k -bufsize {bitrate * 2}k -g {fps * 2} " +
                $"-c:a aac -b:a 64k " +
                $"-metadata:s:a:0 title=player_mic -metadata:s:a:1 title=system_audio " +
                $"-movflags +frag_keyframe+empty_moov+default_base_moof " +
                $"\"{outputPath}\"";
        }

        // 无独显降级模式：仅封装双音轨 AAC，截图由 Worker 每 3 秒输出
        return
            $"-y -hide_banner -loglevel error " +
            $"-f s16le -ar 48000 -ac 1 -i \\\\.\\pipe\\{micPipeName} " +
            $"-f s16le -ar 48000 -ac 2 -i \\\\.\\pipe\\{sysPipeName} " +
            $"-map 0:a -map 1:a " +
            $"-c:a aac -b:a 48k " +
            $"-metadata:s:a:0 title=player_mic -metadata:s:a:1 title=system_audio " +
            $"-movflags +frag_keyframe+empty_moov+default_base_moof -f mp4 " +
            $"\"{outputPath}\"";
    }

    public void WriteVideoFrame(byte[] bgraBytes, int width, int height)
    {
        if (_closing || !_isVideoHw || _vidPipe == null || !_vidPipe.IsConnected) return;
        if (width != _videoWidth || height != _videoHeight) return;

        lock (_writeLock)
        {
            if (_closing) return;
            try
            {
                _vidPipe.Write(bgraBytes, 0, bgraBytes.Length);
            }
            catch
            {
            }
        }
    }

    public bool ShouldTakeDegradedScreenshot(DateTimeOffset now)
    {
        if (_closing || _isVideoHw) return false;
        if ((now - _lastScreenshotAt).TotalSeconds < 3.0) return false;
        _lastScreenshotAt = now;
        return true;
    }

    public string NextScreenshotPath()
    {
        var seq = Interlocked.Increment(ref _screenshotSeq);
        return Path.Combine(_framesDir, $"frame_{seq:D5}.png");
    }

    public void WriteAudioChunks(byte[] micPcm, byte[] sysPcm)
    {
        if (_closing) return;

        lock (_writeLock)
        {
            if (_closing) return;
            try
            {
                if (_micPipe.IsConnected) _micPipe.Write(micPcm, 0, micPcm.Length);
                if (_sysPipe.IsConnected) _sysPipe.Write(sysPcm, 0, sysPcm.Length);
            }
            catch
            {
            }
        }
    }

    public async Task<WorkerEvent> CloseAndFinalizeAsync(CancellationToken ct = default)
    {
        _closing = true;
        var endedAt = DateTimeOffset.UtcNow;

        lock (_writeLock)
        {
            try { _vidPipe?.Flush(); _vidPipe?.Close(); } catch { }
            try { _micPipe.Flush(); _micPipe.Close(); } catch { }
            try { _sysPipe.Flush(); _sysPipe.Close(); } catch { }
        }

        try
        {
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(TimeSpan.FromSeconds(10));
            await _ffmpegProc.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
        }
        catch
        {
            try { _ffmpegProc.Kill(entireProcessTree: true); } catch { }
        }

        var outputPath = _command.OutputPath!;
        long fileSize = 0;
        var sha256Hex = string.Empty;
        if (File.Exists(outputPath))
        {
            var info = new FileInfo(outputPath);
            fileSize = info.Length;
            await using var fs = File.OpenRead(outputPath);
            var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
            sha256Hex = Convert.ToHexString(hash).ToLowerInvariant();
        }

        var durationMs = Math.Max(0, (int)(endedAt - StartedAt).TotalMilliseconds);

        return new WorkerEvent(
            Event: "SEGMENT_CLOSED",
            SegmentId: _command.SegmentId,
            SessionId: _command.SessionId,
            SeqNo: _command.SeqNo,
            IsOrder: _command.IsOrder,
            CodecMode: _command.CodecMode,
            FilePath: outputPath,
            FileSize: fileSize,
            Sha256: sha256Hex,
            DurationMs: durationMs,
            StartedAt: StartedAt,
            EndedAt: endedAt);
    }

    public async ValueTask DisposeAsync()
    {
        _vidPipe?.Dispose();
        _micPipe.Dispose();
        _sysPipe.Dispose();
        _ffmpegProc.Dispose();
        await Task.CompletedTask;
    }
}
