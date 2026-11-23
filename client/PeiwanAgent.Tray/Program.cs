using PeiwanAgent.Core;
using PeiwanAgent.Core.Detection;

namespace PeiwanAgent.Tray;

// 占位托盘壳: 仅演示 Core 的接线,不含录制/上传。
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var options = new AgentOptions();
        var detector = new GameProcessDetector(new SystemProcessProbe(), options.TargetProcesses);
        using var icon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Peiwan Agent (placeholder)",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };
        icon.ContextMenuStrip.Items.Add("退出", null, (_, _) => Application.Exit());
        detector.GameStarted += (_, _) => icon.Text = "Peiwan Agent: 游戏运行中";
        detector.GameExited += (_, _) => icon.Text = "Peiwan Agent: 待机";

        using var timer = new System.Windows.Forms.Timer { Interval = 2000 };
        timer.Tick += (_, _) => detector.Poll();
        timer.Start();

        Application.Run();
    }
}