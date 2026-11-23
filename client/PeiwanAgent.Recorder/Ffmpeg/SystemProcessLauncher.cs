using System.Diagnostics;
using System.Text;

namespace PeiwanAgent.Recorder.Ffmpeg;

/// <summary>基于 System.Diagnostics.Process 的启动器。使用 ArgumentList,由运行时负责转义。</summary>
public sealed class SystemProcessLauncher : IProcessLauncher
{
    public IManagedProcess Start(ProcessSpec spec)
    {
        var psi = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in spec.Arguments) psi.ArgumentList.Add(a);
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var m = new SystemManagedProcess(p);
        try
        {
            p.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            p.Dispose();
            throw new FfmpegNotFoundException(spec.FileName + "(" + ex.Message + ")");
        }
        m.Attach();
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* 尽力而为 */ }
        return m;
    }
}

internal sealed class SystemManagedProcess : IManagedProcess
{
    private readonly Process _p;
    private readonly BoundedText _out = new();
    private readonly BoundedText _err = new();
    private Task? _outTask, _errTask;

    public SystemManagedProcess(Process p) => _p = p;

    public void Attach()
    {
        _outTask = Task.Run(() => Pump(_p.StandardOutput, _out));
        _errTask = Task.Run(() => Pump(_p.StandardError, _err));
    }

    private static void Pump(StreamReader r, BoundedText sink)
    {
        var buf = new char[4096];
        try { int n; while ((n = r.Read(buf, 0, buf.Length)) > 0) sink.Append(buf, n); }
        catch { /* 进程被杀 */ }
    }

    public bool HasExited { get { try { return _p.HasExited; } catch { return true; } } }
    public int? ExitCode { get { try { return _p.HasExited ? _p.ExitCode : null; } catch { return null; } } }

    public void WriteStdin(string text)
    {
        try { _p.StandardInput.Write(text); _p.StandardInput.Flush(); }
        catch (IOException) { }
        catch (InvalidOperationException) { }
    }

    public bool WaitForExit(TimeSpan timeout)
    {
        if (!_p.WaitForExit(timeout)) return false;
        _outTask?.Wait(2000);
        _errTask?.Wait(2000);
        return true;
    }

    public void Kill()
    {
        try { if (!_p.HasExited) _p.Kill(entireProcessTree: true); } catch { }
    }

    public string Stdout => _out.ToString();
    public string Stderr => _err.ToString();

    public void Dispose()
    {
        try { _p.StandardInput.Close(); } catch { }
        _p.Dispose();
    }
}

/// <summary>保留开头 64KB 与末尾 16KB(ffmpeg 的 Duration 在开头,错误在末尾)。</summary>
internal sealed class BoundedText
{
    private const int Head = 64 * 1024, Tail = 16 * 1024;
    private readonly StringBuilder _head = new();
    private readonly StringBuilder _tail = new();
    private readonly object _lock = new();

    public void Append(char[] buf, int n)
    {
        lock (_lock)
        {
            int i = 0;
            if (_head.Length < Head)
            {
                i = Math.Min(n, Head - _head.Length);
                _head.Append(buf, 0, i);
            }
            if (i < n)
            {
                _tail.Append(buf, i, n - i);
                if (_tail.Length > Tail * 2) _tail.Remove(0, _tail.Length - Tail);
            }
        }
    }

    public override string ToString()
    {
        lock (_lock)
        {
            var t = _tail.Length > Tail ? _tail.ToString(_tail.Length - Tail, Tail) : _tail.ToString();
            return _head + t;
        }
    }
}
