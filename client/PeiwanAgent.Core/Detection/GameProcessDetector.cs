namespace PeiwanAgent.Core.Detection;

/// <summary>轮询式游戏进程检测器。调用 Poll() 比较前后状态,边沿触发事件。</summary>
public sealed class GameProcessDetector
{
    private readonly IProcessProbe _probe;
    private readonly HashSet<string> _targets;

    public GameProcessDetector(IProcessProbe probe, IEnumerable<string>? targetProcessNames = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _targets = new HashSet<string>(
            (targetProcessNames ?? new[] { AgentOptions.DefaultGameProcessName }).Select(Normalize),
            StringComparer.OrdinalIgnoreCase);
        if (_targets.Count == 0) throw new ArgumentException("至少需要一个目标进程名", nameof(targetProcessNames));
    }

    public bool IsGameRunning { get; private set; }

    public event EventHandler? GameStarted;
    public event EventHandler? GameExited;

    /// <summary>探测一次。返回本次是否发生状态变化。</summary>
    public bool Poll()
    {
        var running = _probe.GetRunningProcessNames().Any(n => _targets.Contains(Normalize(n)));
        if (running == IsGameRunning) return false;
        IsGameRunning = running;
        (running ? GameStarted : GameExited)?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static string Normalize(string name)
    {
        var t = name.Trim();
        return t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? t[..^4] : t;
    }
}