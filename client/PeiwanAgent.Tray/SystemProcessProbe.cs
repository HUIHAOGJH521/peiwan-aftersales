using System.Diagnostics;
using PeiwanAgent.Core.Detection;

namespace PeiwanAgent.Tray;

/// <summary>仅枚举进程名(Process.GetProcesses),不打开进程句柄读取内存,无注入/钩子。</summary>
internal sealed class SystemProcessProbe : IProcessProbe
{
    public IReadOnlyCollection<string> GetRunningProcessNames()
    {
        var procs = Process.GetProcesses();
        try { return procs.Select(p => p.ProcessName).ToArray(); }
        finally { foreach (var p in procs) p.Dispose(); }
    }
}