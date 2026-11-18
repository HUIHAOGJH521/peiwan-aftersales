using System.Diagnostics;
using PeiwanAgent.Core.Models;

namespace PeiwanAgent.Core.Services;

public interface IProcessMonitor
{
    DetectedProcessInfo? FindRunningGame(IReadOnlyCollection<string> targetProcessNames);
}

/// <summary>
/// 仅通过系统进程列表（ProcessName）判断目标游戏是否运行，
/// 严禁 OpenProcess 读取内存或注入任何模块（规避 ACE 反作弊）。
/// </summary>
public sealed class ProcessMonitor : IProcessMonitor
{
    public static string NormalizeName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }

    public static bool IsMatch(string runningProcessName, IReadOnlyCollection<string> targetProcessNames)
    {
        var normalizedRunning = NormalizeName(runningProcessName);
        foreach (var target in targetProcessNames)
        {
            if (string.Equals(normalizedRunning, NormalizeName(target), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public DetectedProcessInfo? FindRunningGame(IReadOnlyCollection<string> targetProcessNames)
    {
        if (targetProcessNames.Count == 0)
        {
            return null;
        }

        var processes = Process.GetProcesses();
        try
        {
            foreach (var proc in processes)
            {
                try
                {
                    if (!IsMatch(proc.ProcessName, targetProcessNames))
                    {
                        continue;
                    }

                    nint hwnd = nint.Zero;
                    string title = string.Empty;
                    try
                    {
                        hwnd = proc.MainWindowHandle;
                        title = proc.MainWindowTitle;
                    }
                    catch
                    {
                        // 某些受保护进程或启动过渡期可能无权读取窗口句柄，退化为屏幕采集
                    }

                    return new DetectedProcessInfo(proc.Id, proc.ProcessName, hwnd, title);
                }
                catch
                {
                    // 忽略已退出进程
                }
            }

            return null;
        }
        finally
        {
            foreach (var proc in processes)
            {
                proc.Dispose();
            }
        }
    }
}
