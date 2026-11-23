using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PeiwanAgent.Recorder.Audio;

/// <summary>源格式(16-bit PCM / 32-bit float,任意声道)→ 48k 单声道 16-bit。无设备依赖。</summary>
public sealed class PcmPipeline
{
    private readonly WaveFormat _src;
    private readonly BufferedWaveProvider? _buf;
    private readonly ISampleProvider? _resampler;
    private float[] _scratch = new float[4096];

    public PcmPipeline(WaveFormat source, int targetRate = AudioFormat.SampleRate)
    {
        if (source.Encoding == WaveFormatEncoding.Extensible)
            source = WaveFormat.CreateCustomFormat(
                source.BitsPerSample == 32 && IsFloat(source) ? WaveFormatEncoding.IeeeFloat : WaveFormatEncoding.Pcm,
                source.SampleRate, source.Channels, source.AverageBytesPerSecond, source.BlockAlign, source.BitsPerSample);
        bool ok = (source.Encoding == WaveFormatEncoding.IeeeFloat && source.BitsPerSample == 32)
               || (source.Encoding == WaveFormatEncoding.Pcm && source.BitsPerSample == 16);
        if (!ok) throw new NotSupportedException($"不支持的采集格式: {source}");
        _src = source;
        TargetRate = targetRate;
        if (source.SampleRate != targetRate)
        {
            _buf = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(source.SampleRate, 1))
            { ReadFully = false, BufferDuration = TimeSpan.FromSeconds(10), DiscardOnBufferOverflow = true };
            _resampler = new WdlResamplingSampleProvider(_buf.ToSampleProvider(), targetRate);
        }
    }

    private static bool IsFloat(WaveFormat f) => f is WaveFormatExtensible e && e.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");

    public int TargetRate { get; }
    public bool NeedsResample => _resampler != null;

    /// <summary>多声道平均混为单声道。</summary>
    public static float[] MixToMono(ReadOnlySpan<float> interleaved, int channels)
    {
        if (channels <= 1) return interleaved.ToArray();
        int frames = interleaved.Length / channels;
        var o = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            float s = 0;
            for (int c = 0; c < channels; c++) s += interleaved[i * channels + c];
            o[i] = s / channels;
        }
        return o;
    }

    public static short FloatToPcm16(float v)
    {
        v = Math.Clamp(v, -1f, 1f);
        return (short)Math.Round(v * 32767f);
    }

    public short[] Process(byte[] data, int count)
    {
        int bps = _src.BitsPerSample / 8;
        int n = count / bps / _src.Channels * _src.Channels; // 整帧
        if (_scratch.Length < n) _scratch = new float[n];
        if (_src.Encoding == WaveFormatEncoding.IeeeFloat)
            for (int i = 0; i < n; i++) _scratch[i] = BitConverter.ToSingle(data, i * 4);
        else
            for (int i = 0; i < n; i++) _scratch[i] = BitConverter.ToInt16(data, i * 2) / 32768f;

        var mono = MixToMono(_scratch.AsSpan(0, n), _src.Channels);
        if (_resampler == null)
        {
            var r = new short[mono.Length];
            for (int i = 0; i < r.Length; i++) r[i] = FloatToPcm16(mono[i]);
            return r;
        }
        var bytes = new byte[mono.Length * 4];
        Buffer.BlockCopy(mono, 0, bytes, 0, bytes.Length);
        _buf!.AddSamples(bytes, 0, bytes.Length);
        int expect = (int)((long)mono.Length * TargetRate / _src.SampleRate) + 16;
        var tmp = new float[expect];
        int got = _resampler.Read(tmp, 0, expect);
        var res = new short[got];
        for (int i = 0; i < got; i++) res[i] = FloatToPcm16(tmp[i]);
        return res;
    }
}
