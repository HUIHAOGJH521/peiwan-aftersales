namespace PeiwanAgent.Recorder.Ffmpeg;

public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments);

/// <summary>被启动的子进程句柄(ffmpeg)。</summary>
public interface IManagedProcess : IDisposable
{
    bool HasExited { get; }
    int? ExitCode { get; }
    /// <summary>向 stdin 写入文本(如 "q")。进程已退出时静默忽略。</summary>
    void WriteStdin(string text);
    bool WaitForExit(TimeSpan timeout);
    void Kill();
    string Stdout { get; }
    string Stderr { get; }
}

public interface IProcessLauncher
{
    IManagedProcess Start(ProcessSpec spec);
}

public sealed record FfmpegResult(int ExitCode, string Stdout, string Stderr, bool TimedOut = false)
{
    public bool Success => ExitCode == 0 && !TimedOut;
}

/// <summary>对 ffmpeg 的调用抽象,便于单测替换。</summary>
public interface IFfmpegRunner
{
    string FfmpegPath { get; }
    /// <summary>同步运行到结束(探测、封装等短任务)。超时则杀进程。</summary>
    FfmpegResult Run(IReadOnlyList<string> args, TimeSpan timeout);
    /// <summary>启动长时间运行的 ffmpeg(录制)。</summary>
    IManagedProcess Start(IReadOnlyList<string> args);
}

public sealed class FfmpegNotFoundException : Exception
{
    public FfmpegNotFoundException(string configured)
        : base($"找不到 ffmpeg:\"{configured}\"。请在配置中指定 ffmpeg.exe 的完整路径,或将其加入 PATH。") { }
}

public sealed class RecorderException : Exception
{
    public RecorderException(string message, Exception? inner = null) : base(message, inner) { }
}
