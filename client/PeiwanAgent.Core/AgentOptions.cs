namespace PeiwanAgent.Core;

/// <summary>默认值与 /api/v1/client/config 的字段对应(architecture.md 5.2)。</summary>
public sealed record AgentOptions
{
    public const string DefaultGameProcessName = "DeltaForceClient-Win64-Shipping.exe";

    public IReadOnlyList<string> TargetProcesses { get; init; } = new[] { DefaultGameProcessName };
    public int PostExitDelaySec { get; init; } = 30;
    public int IdleThresholdSec { get; init; } = 60;
    public int LocalRetentionDays { get; init; } = 3;
}