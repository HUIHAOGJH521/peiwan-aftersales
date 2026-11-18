using System.Diagnostics;
using PeiwanAgent.Core.Models;

namespace PeiwanAgent.Core.Services;

public sealed record EncoderProbeResult(CodecMode Mode, string? HwEncoderName);

public interface IEncoderProbe
{
    Task<EncoderProbeResult> ProbeBestEncoderAsync(string ffmpegPath, CancellationToken ct = default);
}

/// <summary>
/// 实际运行 1 帧空测试探测可用硬件编码器（NVENC -> AMF -> QSV），
/// 若均不可用则自动降级为 AudioScreenshots 模式，避免 CPU 软编抢占游戏性能。
/// </summary>
public sealed class EncoderProbe : IEncoderProbe
{
    private static readonly string[] CandidateEncoders = ["h264_nvenc", "h264_amf", "h264_qsv"];

    public async Task<EncoderProbeResult> ProbeBestEncoderAsync(string ffmpegPath, CancellationToken ct = default)
    {
        foreach (var encoder in CandidateEncoders)
        {
            if (await CanEncodeOneFrameAsync(ffmpegPath, encoder, ct).ConfigureAwait(false))
            {
                return new EncoderProbeResult(CodecMode.VideoHw, encoder);
            }
        }

        return new EncoderProbeResult(CodecMode.AudioScreenshots, null);
    }

    private static async Task<bool> CanEncodeOneFrameAsync(string ffmpegPath, string encoder, CancellationToken ct)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-hide_banner -loglevel error -f lavfi -i color=c=black:s=640x360 -frames:v 1 -c:v {encoder} -f null -",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            if (!proc.Start())
            {
                return false;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
