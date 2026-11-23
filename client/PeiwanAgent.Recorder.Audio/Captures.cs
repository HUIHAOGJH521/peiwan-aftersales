using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PeiwanAgent.Core.Recording;

namespace PeiwanAgent.Recorder.Audio;

/// <summary>WASAPI 采集基类:挂钟补静音、首帧偏移、设备变更恢复。仅用公开 WASAPI 接口。</summary>
public abstract class WasapiTrackCaptureBase : ITrackCapture
{
    private readonly object _gate = new();
    private readonly SilencePadder _padder = new();
    private readonly List<string> _events = new();
    private IWaveIn? _capture;
    private PcmPipeline? _pipe;
    private WaveFileWriter? _writer;
    private Timer? _timer;
    private Stopwatch _sw = new();
    private DateTimeOffset _startWall;
    private long _written, _silence;
    private double _firstOffsetMs = -1;
    private int _restarts;
    private bool _running, _needRestart, _gotData;
    private DateTime _lastRestartTry;

    protected abstract DataFlow Flow { get; }
    protected abstract string DeviceKind { get; }
    protected abstract IWaveIn CreateCapture(MMDevice device);

    public IReadOnlyList<string> Events { get { lock (_gate) return _events.ToArray(); } }

    public CaptureStats GetStats()
    {
        lock (_gate)
            return new CaptureStats(_startWall, _written, _silence, _firstOffsetMs < 0 ? double.NaN : _firstOffsetMs, _restarts);
    }

    private MMDevice GetDevice()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            return en.GetDefaultAudioEndpoint(Flow, Role.Console);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无可用的{DeviceKind}设备(默认{(Flow == DataFlow.Capture ? "录音" : "播放")}设备不存在或不可用): {ex.Message}", ex);
        }
    }

    private void OpenCapture()
    {
        using var dev = GetDevice();
        var cap = CreateCapture(dev);
        var pipe = new PcmPipeline(cap.WaveFormat);
        cap.DataAvailable += OnData;
        cap.RecordingStopped += OnStopped;
        try { cap.StartRecording(); }
        catch (Exception ex)
        {
            cap.Dispose();
            throw new InvalidOperationException($"无法启动{DeviceKind}采集: {ex.Message}", ex);
        }
        _capture = cap;
        _pipe = pipe;
    }

    public void Start(string wavPath)
    {
        lock (_gate)
        {
            if (_running) throw new InvalidOperationException("已在采集中");
            _written = _silence = 0; _firstOffsetMs = -1; _restarts = 0; _events.Clear(); _needRestart = false; _gotData = false;
            var dir = Path.GetDirectoryName(Path.GetFullPath(wavPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _writer = new WaveFileWriter(wavPath, new WaveFormat(AudioFormat.SampleRate, 16, 1));
            _startWall = DateTimeOffset.UtcNow;
            _sw = Stopwatch.StartNew();
            try { OpenCapture(); }
            catch
            {
                _writer.Dispose(); _writer = null;
                try { File.Delete(wavPath); } catch { }
                throw;
            }
            _running = true;
            _timer = new Timer(_ => Tick(), null, 20, 20);
        }
    }

    private void OnData(object? s, WaveInEventArgs e)
    {
        lock (_gate)
        {
            if (!_running || _writer == null || _pipe == null || e.BytesRecorded == 0) return;
            if (_firstOffsetMs < 0) _firstOffsetMs = _sw.Elapsed.TotalMilliseconds;
            // 首帧之前的空档先补静音,保证首帧落在正确时间位置
            var pcm = _pipe.Process(e.Buffer, e.BytesRecorded);
            // 本块数据视为恰好在当前时刻结束,故只补 (挂钟 - 本块长度)
            if (!_gotData)
            {
                _gotData = true;
                long gap = _padder.ExpectedSamples(_sw.Elapsed) - pcm.Length - _written;
                if (gap >= 0) PadLocked(gap);
                else pcm = pcm[(int)Math.Min(-gap, pcm.Length)..]; // 数据早于 Start,丢弃多出的前段
            }
            WriteLocked(pcm);
        }
    }

    private void WriteLocked(short[] pcm)
    {
        if (pcm.Length == 0) return;
        _writer!.WriteSamples(pcm, 0, pcm.Length);
        _written += pcm.Length;
    }

    private void PadLocked(long n)
    {
        while (n > 0)
        {
            int chunk = (int)Math.Min(n, 48000);
            _writer!.WriteSamples(new short[chunk], 0, chunk);
            _written += chunk; _silence += chunk; n -= chunk;
        }
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (!_running || _writer == null) return;
            try
            {
                PadLocked(_padder.ComputePad(_sw.Elapsed, _written));
                if (_needRestart && (DateTime.UtcNow - _lastRestartTry).TotalMilliseconds >= 1000)
                {
                    _lastRestartTry = DateTime.UtcNow;
                    TryRestartLocked();
                }
            }
            catch (Exception ex) { _events.Add($"{_sw.Elapsed.TotalSeconds:F2}s tick 异常: {ex.Message}"); }
        }
    }

    private void OnStopped(object? s, StoppedEventArgs e)
    {
        lock (_gate)
        {
            if (!_running || !ReferenceEquals(s, _capture)) return;
            _events.Add($"{_sw.Elapsed.TotalSeconds:F2}s 设备采集中断: {e.Exception?.Message ?? "设备停止"},尝试恢复(期间补静音)");
            DisposeCaptureLocked();
            _needRestart = true;
        }
    }

    private void TryRestartLocked()
    {
        try
        {
            OpenCapture();
            _needRestart = false; _restarts++;
            _events.Add($"{_sw.Elapsed.TotalSeconds:F2}s 设备已恢复");
        }
        catch (Exception ex) { _events.Add($"{_sw.Elapsed.TotalSeconds:F2}s 恢复失败: {ex.Message}"); }
    }

    private void DisposeCaptureLocked()
    {
        var c = _capture; _capture = null; _pipe = null;
        if (c == null) return;
        c.DataAvailable -= OnData;
        c.RecordingStopped -= OnStopped;
        // 在线程池释放,避免在回调线程内死锁
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { c.StopRecording(); } catch { }
            try { c.Dispose(); } catch { }
        });
    }

    public void Stop()
    {
        IWaveIn? c; Timer? t; WaveFileWriter? w;
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
            t = _timer; _timer = null;
            c = _capture; _capture = null; _pipe = null;
            w = _writer; _writer = null;
            if (c != null) { c.DataAvailable -= OnData; c.RecordingStopped -= OnStopped; }
            try { w!.Flush(); } catch { }
            // 精确补到挂钟总时长
            _final = _sw.Elapsed;
        }
        t?.Dispose();
        try { c?.StopRecording(); } catch { }
        try { c?.Dispose(); } catch { }
        lock (_gate)
        {
            long pad = _padder.ComputeFinalPad(_final, _written);
            while (pad > 0)
            {
                int chunk = (int)Math.Min(pad, 48000);
                w!.WriteSamples(new short[chunk], 0, chunk);
                _written += chunk; _silence += chunk; pad -= chunk;
            }
            w!.Dispose();
        }
    }
    private TimeSpan _final;

    public void Dispose() => Stop();
}

/// <summary>默认录音设备(麦克风)。</summary>
public sealed class MicTrackCapture : WasapiTrackCaptureBase
{
    protected override DataFlow Flow => DataFlow.Capture;
    protected override string DeviceKind => "麦克风";
    protected override IWaveIn CreateCapture(MMDevice device) => new WasapiCapture(device, false, 20);
}

/// <summary>默认渲染设备 loopback(系统声音)。</summary>
public sealed class LoopbackTrackCapture : WasapiTrackCaptureBase
{
    protected override DataFlow Flow => DataFlow.Render;
    protected override string DeviceKind => "系统声音(loopback)";
    protected override IWaveIn CreateCapture(MMDevice device) => new WasapiLoopbackCapture(device);
}



