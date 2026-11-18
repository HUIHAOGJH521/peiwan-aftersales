namespace PeiwanAgent.Core.Config;

public sealed class AgentConfig
{
    public string StudioCode { get; set; } = "pilot_01";
    public string ServerBaseUrl { get; set; } = "http://localhost:8000";

    /// <summary>
    /// 目标游戏进程名列表（支持带或不带 .exe，运行期可通过 /api/v1/client/config 热更新）
    /// </summary>
    public List<string> TargetProcessNames { get; set; } =
    [
        "DeltaForceClient-Win64-Shipping.exe",
        "DeltaForceClient.exe"
    ];

    public int SegmentDurationSeconds { get; set; } = 300;
    public int LocalRetentionDays { get; set; } = 3;
    public int IdleThresholdSeconds { get; set; } = 60;
    public int PostExitDelaySeconds { get; set; } = 30;
    public int ProcessPollIntervalMs { get; set; } = 2000;
    public int TargetFps { get; set; } = 15;
    public int VideoBitrateKbps { get; set; } = 850;
    public string FfmpegPath { get; set; } = "ffmpeg";

    public string DataRootDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PeiwanAftersales");

    public string RecordingsDir => Path.Combine(DataRootDir, "recordings");
    public string DatabasePath => Path.Combine(DataRootDir, "agent.db");
}
