using System.Text.Json;
using PeiwanAgent.Core.Models;
using PeiwanAgent.Core.Services;
using PeiwanRecorder.Worker.Audio;
using PeiwanRecorder.Worker.Encoding;
using PeiwanRecorder.Worker.Video;

namespace PeiwanRecorder.Worker;

internal static class Program
{
    private static readonly object StdoutLock = new();

    private static async Task<int> Main(string[] args)
    {
        var ffmpegPath = ParseArg(args, "--ffmpeg") ?? "ffmpeg";

        if (args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            var secondsStr = ParseArg(args, "--seconds") ?? "5";
            var outPath = ParseArg(args, "--out") ?? Path.Combine(Path.GetTempPath(), $"peiwan_smoke_{DateTime.Now:HHmmss}.mp4");
            var seconds = int.TryParse(secondsStr, out var s) ? Math.Clamp(s, 2, 30) : 5;
            return await RunSmokeTestAsync(ffmpegPath, seconds, outPath).ConfigureAwait(false);
        }

        return await RunIpcLoopAsync(ffmpegPath).ConfigureAwait(false);
    }

    private static async Task<int> RunSmokeTestAsync(string ffmpegPath, int seconds, string outPath)
    {
        var probe = new EncoderProbe();
        var encoder = await probe.ProbeBestEncoderAsync(ffmpegPath).ConfigureAwait(false);
        Console.WriteLine($"[SmokeTest] Probe result: Mode={encoder.Mode}, Encoder={encoder.HwEncoderName ?? "none"}");

        using var video = new WgcFrameCapturer();
        using var audio = new DualTrackAudioCapturer();
        video.Start(nint.Zero);
        audio.Start();

        // 等待 WGC 捕获出首帧尺寸
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while ((video.Width <= 0 || video.Height <= 0) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }

        var w = video.Width > 0 ? video.Width : 1280;
        var h = video.Height > 0 ? video.Height : 720;

        var cmd = new WorkerCommand(
            Cmd: "START",
            SegmentId: Guid.NewGuid().ToString(),
            OutputPath: outPath,
            CodecMode: encoder.Mode == CodecMode.VideoHw ? "video_hw" : "audio_screenshots",
            HwEncoder: encoder.HwEncoderName,
            Fps: 15,
            BitrateKbps: 850,
            IsOrder: true,
            SessionId: Guid.NewGuid().ToString(),
            SeqNo: 1);

        await using var writer = await FfmpegSegmentWriter.StartAsync(cmd, ffmpegPath, w, h).ConfigureAwait(false);
        using var pumpCts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));

        audio.OnPcmChunk += (mic, sys) => writer.WriteAudioChunks(mic, sys);

        var videoTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / 15));
            byte[]? lastFrame = null;
            try
            {
                while (await timer.WaitForNextTickAsync(pumpCts.Token).ConfigureAwait(false))
                {
                    if (encoder.Mode == CodecMode.VideoHw)
                    {
                        if (video.TryCopyLatestFrame(out var frame, out var fw, out var fh) && fw == w && fh == h)
                        {
                            lastFrame = frame;
                        }
                        if (lastFrame != null)
                        {
                            writer.WriteVideoFrame(lastFrame, w, h);
                        }
                    }
                    else if (writer.ShouldTakeDegradedScreenshot(DateTimeOffset.UtcNow))
                    {
                        video.SaveSnapshotPng(writer.NextScreenshotPath());
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        await videoTask.ConfigureAwait(false);
        var closed = await writer.CloseAndFinalizeAsync().ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(closed));
        return 0;
    }

    private static async Task<int> RunIpcLoopAsync(string ffmpegPath)
    {
        using var video = new WgcFrameCapturer();
        using var audio = new DualTrackAudioCapturer();
        FfmpegSegmentWriter? currentWriter = null;
        var writerLock = new object();
        byte[]? cachedFrame = null;
        int targetW = 1280;
        int targetH = 720;
        int currentFps = 15;
        bool isVideoHw = true;
        bool captureStarted = false;

        audio.OnPcmChunk += (mic, sys) =>
        {
            lock (writerLock)
            {
                currentWriter?.WriteAudioChunks(mic, sys);
            }
        };

        using var videoLoopCts = new CancellationTokenSource();
        var videoLoopTask = Task.Run(async () =>
        {
            var intervalMs = Math.Max(33, 1000 / Math.Clamp(currentFps, 5, 30));
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
            try
            {
                while (await timer.WaitForNextTickAsync(videoLoopCts.Token).ConfigureAwait(false))
                {
                    FfmpegSegmentWriter? active;
                    lock (writerLock)
                    {
                        active = currentWriter;
                    }
                    if (active == null) continue;

                    if (isVideoHw)
                    {
                        if (video.TryCopyLatestFrame(out var frame, out var fw, out var fh) && fw == targetW && fh == targetH)
                        {
                            cachedFrame = frame;
                        }
                        if (cachedFrame != null)
                        {
                            active.WriteVideoFrame(cachedFrame, targetW, targetH);
                        }
                    }
                    else if (active.ShouldTakeDegradedScreenshot(DateTimeOffset.UtcNow))
                    {
                        video.SaveSnapshotPng(active.NextScreenshotPath());
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        Emit(new WorkerEvent("READY", Message: "PeiwanRecorder.Worker initialized"));

        while (true)
        {
            var line = await Console.In.ReadLineAsync().ConfigureAwait(false);
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            WorkerCommand? cmd;
            try
            {
                cmd = JsonSerializer.Deserialize<WorkerCommand>(line);
            }
            catch (Exception ex)
            {
                Emit(new WorkerEvent("ERROR", Message: $"Invalid command JSON: {ex.Message}"));
                continue;
            }

            if (cmd == null) continue;

            if (cmd.Cmd == "STOP")
            {
                FfmpegSegmentWriter? toClose;
                lock (writerLock)
                {
                    toClose = currentWriter;
                    currentWriter = null;
                }
                if (toClose != null)
                {
                    var evt = await toClose.CloseAndFinalizeAsync().ConfigureAwait(false);
                    await toClose.DisposeAsync().ConfigureAwait(false);
                    Emit(evt);
                }
                break;
            }

            if (cmd.Cmd is "START" or "SPLIT")
            {
                if (!captureStarted)
                {
                    video.Start((nint)cmd.TargetHwnd);
                    audio.Start();
                    captureStarted = true;

                    var waitDeadline = DateTimeOffset.UtcNow.AddSeconds(3);
                    while ((video.Width <= 0 || video.Height <= 0) && DateTimeOffset.UtcNow < waitDeadline)
                    {
                        await Task.Delay(50).ConfigureAwait(false);
                    }
                }

                FfmpegSegmentWriter? prevWriter;
                lock (writerLock)
                {
                    prevWriter = currentWriter;
                    currentWriter = null;
                }

                if (prevWriter != null)
                {
                    var closedEvt = await prevWriter.CloseAndFinalizeAsync().ConfigureAwait(false);
                    await prevWriter.DisposeAsync().ConfigureAwait(false);
                    Emit(closedEvt);
                }

                targetW = video.Width > 0 ? video.Width : 1280;
                targetH = video.Height > 0 ? video.Height : 720;
                currentFps = cmd.Fps > 0 ? cmd.Fps : 15;
                isVideoHw = !string.Equals(cmd.CodecMode, "audio_screenshots", StringComparison.OrdinalIgnoreCase);

                var nextWriter = await FfmpegSegmentWriter.StartAsync(cmd, ffmpegPath, targetW, targetH).ConfigureAwait(false);
                if (isVideoHw)
                {
                    if (video.TryCopyLatestFrame(out var initFrame, out var iw, out var ih) && iw == targetW && ih == targetH)
                    {
                        cachedFrame = initFrame;
                    }
                    if (cachedFrame != null)
                    {
                        nextWriter.WriteVideoFrame(cachedFrame, targetW, targetH);
                    }
                }
                lock (writerLock)
                {
                    currentWriter = nextWriter;
                }
                Emit(new WorkerEvent("SEGMENT_STARTED", SegmentId: cmd.SegmentId, SessionId: cmd.SessionId, SeqNo: cmd.SeqNo, IsOrder: cmd.IsOrder));
            }
        }

        videoLoopCts.Cancel();
        try { await videoLoopTask.ConfigureAwait(false); } catch { }
        return 0;
    }

    private static void Emit(WorkerEvent evt)
    {
        var json = JsonSerializer.Serialize(evt);
        lock (StdoutLock)
        {
            Console.WriteLine(json);
            Console.Out.Flush();
        }
    }

    private static string? ParseArg(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }
}
