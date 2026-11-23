namespace PeiwanAgent.Core.Detection;

/// <summary>
/// 只返回当前运行的进程名列表。实现只能使用系统公开的进程枚举,
/// 严禁 OpenProcess 读内存、注入、钩子(ADR 0004)。
/// </summary>
public interface IProcessProbe
{
    IReadOnlyCollection<string> GetRunningProcessNames();
}