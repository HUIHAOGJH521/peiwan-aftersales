using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PeiwanRecorder.Worker.Audio;

/// <summary>
/// 通过 WASAPI 分别采集：
/// - 音轨 1（Track 1）：陪玩师麦克风（统一输出 48kHz / 16-bit PCM / 单声道）
/// - 音轨 2（Track 2）：系统播放回环（统一输出 48kHz / 16-bit PCM / 立体声）
/// 并通过固定周期静音补齐保持双轨与视频时间轴严格同步。
/// </summary>
public sealed class DualTrackAudioCapturer : IDisposable
{
    public const int TargetSampleRate = 48000;
    private const int ChunkMs = 20;
    private const int MicBytesPerChunk = TargetSampleRate * 1 * 2 * ChunkMs / 1000; // 1920 bytes
    private const int SysBytesPerChunk = TargetSampleRate * 2 * 2 * ChunkMs / 1000; // 3840 bytes

    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _sysCapture;
    private BufferedWaveProvider? _micBuffer;
    private BufferedWaveProvider? _sysBuffer;
    private IWaveProvider? _micResampled;
    private IWaveProvider? _sysResampled;

    private CancellationTokenSource? _cts;
    private Task? _pumpTask;

    public event Action<byte[], byte[]>? OnPcmChunk;

    public void Start()
    {
        SetupMicCapture();
        SetupSystemLoopbackCapture();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _pumpTask = Task.Run(() => PumpLoopAsync(token), token);

        try { _micCapture?.StartRecording(); } catch { }
        try { _sysCapture?.StartRecording(); } catch { }
    }

    private void SetupMicCapture()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            _micCapture = new WasapiCapture(device);
            var srcFormat = _micCapture.WaveFormat;
            _micBuffer = new BufferedWaveProvider(srcFormat)
            {
                DiscardOnBufferOverflow = true,
                ReadFully = false,
                BufferDuration = TimeSpan.FromSeconds(2)
            };
            _micCapture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded > 0)
                {
                    _micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                }
            };
            _micResampled = BuildTargetWaveProvider(_micBuffer, targetChannels: 1);
        }
        catch
        {
            // 无可用麦克风时退化为静音音轨，避免中断录制
            _micCapture = null;
        }
    }

    private void SetupSystemLoopbackCapture()
    {
        try
        {
            _sysCapture = new WasapiLoopbackCapture();
            var srcFormat = _sysCapture.WaveFormat;
            _sysBuffer = new BufferedWaveProvider(srcFormat)
            {
                DiscardOnBufferOverflow = true,
                ReadFully = false,
                BufferDuration = TimeSpan.FromSeconds(2)
            };
            _sysCapture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded > 0)
                {
                    _sysBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                }
            };
            _sysResampled = BuildTargetWaveProvider(_sysBuffer, targetChannels: 2);
        }
        catch
        {
            _sysCapture = null;
        }
    }

    private static IWaveProvider BuildTargetWaveProvider(BufferedWaveProvider source, int targetChannels)
    {
        ISampleProvider sample = source.ToSampleProvider();

        if (sample.WaveFormat.Channels > 1 && targetChannels == 1)
        {
            // 多声道麦克风混合为单声道
            var mux = new MultiplexingSampleProvider([sample], 1);
            mux.ConnectInputToOutput(0, 0);
            sample = mux;
        }
        else if (sample.WaveFormat.Channels == 1 && targetChannels == 2)
        {
            sample = new MonoToStereoSampleProvider(sample);
        }
        else if (sample.WaveFormat.Channels > 2 && targetChannels == 2)
        {
            var mux = new MultiplexingSampleProvider([sample], 2);
            mux.ConnectInputToOutput(0, 0);
            mux.ConnectInputToOutput(1, 1);
            sample = mux;
        }

        if (sample.WaveFormat.SampleRate != TargetSampleRate)
        {
            sample = new WdlResamplingSampleProvider(sample, TargetSampleRate);
        }

        return new SampleToWaveProvider16(sample);
    }

    private async Task PumpLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(ChunkMs));
        var micChunk = new byte[MicBytesPerChunk];
        var sysChunk = new byte[SysBytesPerChunk];

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            FillChunk(_micResampled, _micBuffer, micChunk);
            FillChunk(_sysResampled, _sysBuffer, sysChunk);
            OnPcmChunk?.Invoke(micChunk, sysChunk);
        }
    }

    private static void FillChunk(IWaveProvider? resampled, BufferedWaveProvider? buffer, byte[] dest)
    {
        Array.Clear(dest);
        if (resampled == null || buffer == null || buffer.BufferedBytes == 0)
        {
            return;
        }

        try
        {
            // 仅当缓冲区足够支撑一次读取时提取，不足部分保留零静音防阻塞
            var bytesRead = resampled.Read(dest, 0, dest.Length);
            if (bytesRead < dest.Length)
            {
                Array.Clear(dest, bytesRead, dest.Length - bytesRead);
            }
        }
        catch
        {
            Array.Clear(dest);
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _micCapture?.StopRecording(); } catch { }
        try { _sysCapture?.StopRecording(); } catch { }
        _micCapture?.Dispose();
        _sysCapture?.Dispose();
        _cts?.Dispose();
    }
}
