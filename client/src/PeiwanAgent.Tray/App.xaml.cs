using System.Drawing;
using System.IO;
using System.Windows;
using PeiwanAgent.Core.Config;
using PeiwanAgent.Core.Models;
using PeiwanAgent.Core.Services;
using PeiwanAgent.Core.Storage;
using Forms = System.Windows.Forms;

namespace PeiwanAgent.Tray;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _trayIcon;
    private LocalStore? _store;
    private RecordingCoordinator? _coordinator;
    private MiniStatusWindow? _statusWindow;
    private CancellationTokenSource? _pollCts;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var config = new AgentConfig();
        Directory.CreateDirectory(config.DataRootDir);
        _store = new LocalStore(config.DatabasePath);

        var workerExe = Path.Combine(AppContext.BaseDirectory, "PeiwanRecorder.Worker.exe");
        if (!File.Exists(workerExe))
        {
            var sibling = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "PeiwanRecorder.Worker", "bin", "Debug", "net8.0-windows10.0.19041.0", "PeiwanRecorder.Worker.exe"));
            if (File.Exists(sibling))
            {
                workerExe = sibling;
            }
        }

        var supervisor = new WorkerSupervisor(workerExe, config.FfmpegPath);
        _coordinator = new RecordingCoordinator(
            config,
            _store,
            new ProcessMonitor(),
            new EncoderProbe(),
            supervisor);

        _statusWindow = new MiniStatusWindow(_coordinator, _store);
        SetupTrayIcon();

        _coordinator.StateChanged += state => Dispatcher.Invoke(() =>
        {
            UpdateTrayTooltip(state);
            _statusWindow?.RefreshUi();
        });
        _coordinator.StatusMessage += msg => Dispatcher.Invoke(() =>
        {
            _statusWindow?.SetMessage(msg);
        });

        await _coordinator.InitializeAsync();
        _statusWindow.Show();

        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(config.ProcessPollIntervalMs));
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    await _coordinator.TickAsync(ct: token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private void SetupTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示状态小窗", null, (_, _) => ShowStatusWindow());
        menu.Items.Add("本地记录 / 漏录补传", null, (_, _) => OpenBackfillWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出助手", null, async (_, _) => await ExitAppAsync());

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "三角洲陪玩售后助手 - 待机中",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowStatusWindow();
    }

    private void UpdateTrayTooltip(RecordingState state)
    {
        if (_trayIcon == null) return;
        _trayIcon.Text = state switch
        {
            RecordingState.RecordingOrder => "陪玩售后助手 - 接单录制中（结束后上传）",
            RecordingState.RecordingPrivate => "陪玩售后助手 - 私人本地录制中（不上云）",
            RecordingState.Paused => "陪玩售后助手 - 录制已暂停",
            _ => "陪玩售后助手 - 待机（等待游戏启动）"
        };
    }

    private void ShowStatusWindow()
    {
        if (_statusWindow == null) return;
        _statusWindow.Show();
        _statusWindow.Activate();
    }

    private void OpenBackfillWindow()
    {
        if (_coordinator == null || _store == null) return;
        var dlg = new BackfillWindow(_coordinator, _store);
        dlg.ShowDialog();
    }

    private async Task ExitAppAsync()
    {
        _pollCts?.Cancel();
        if (_coordinator != null)
        {
            await _coordinator.DisposeAsync();
        }
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        Shutdown();
    }
}
