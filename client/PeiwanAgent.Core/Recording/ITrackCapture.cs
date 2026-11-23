namespace PeiwanAgent.Core.Recording;

/// <summary>单条音轨采集:写出 16-bit PCM WAV 文件。一个实例可重复 Start/Stop,每次写新文件。</summary>
public interface ITrackCapture : IDisposable
{
    /// <summary>开始采集并写入 wavPath。设备不可用时抛 InvalidOperationException。</summary>
    void Start(string wavPath);
    /// <summary>停止并刷新文件头。幂等。</summary>
    void Stop();
}
