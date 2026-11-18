using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using PeiwanAgent.Core.Models;
using PeiwanAgent.Core.Services;
using PeiwanAgent.Core.Storage;
using MediaColor = System.Windows.Media.Color;

namespace PeiwanAgent.Tray;

public partial class MiniStatusWindow : Window
{
    private readonly RecordingCoordinator _coordinator;
    private readonly LocalStore _store;

    public MiniStatusWindow(RecordingCoordinator coordinator, LocalStore store)
    {
        InitializeComponent();
        _coordinator = coordinator;
        _store = store;

        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 16;
        Top = area.Bottom - Height - 16;

        OrderRefBox.TextChanged += (_, _) =>
            OrderRefHint.Visibility = string.IsNullOrEmpty(OrderRefBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        NoteBox.TextChanged += (_, _) =>
            NoteHint.Visibility = string.IsNullOrEmpty(NoteBox.Text) ? Visibility.Visible : Visibility.Collapsed;

        RefreshUi();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 点击关闭仅隐藏到系统托盘，不中断录制
        e.Cancel = true;
        Hide();
    }

    public void SetMessage(string msg)
    {
        HintText.Text = msg;
    }

    public void RefreshUi()
    {
        var state = _coordinator.State;
        switch (state)
        {
            case RecordingState.Idle:
                StatusDot.Fill = new SolidColorBrush(MediaColor.FromRgb(139, 148, 158));
                StatusTitle.Text = "待机（等待游戏启动）";
                ToggleOrderBtn.Content = "开始接单";
                ToggleOrderBtn.Background = new SolidColorBrush(MediaColor.FromRgb(35, 134, 54));
                PauseBtn.Content = "暂停";
                PauseBtn.IsEnabled = false;
                break;
            case RecordingState.RecordingPrivate:
                StatusDot.Fill = new SolidColorBrush(MediaColor.FromRgb(56, 139, 253));
                StatusTitle.Text = "私人录制中（仅本地 3 天，不上云）";
                ToggleOrderBtn.Content = "开始接单";
                ToggleOrderBtn.Background = new SolidColorBrush(MediaColor.FromRgb(35, 134, 54));
                PauseBtn.Content = "暂停";
                PauseBtn.IsEnabled = true;
                break;
            case RecordingState.RecordingOrder:
                StatusDot.Fill = new SolidColorBrush(MediaColor.FromRgb(248, 81, 73));
                StatusTitle.Text = "接单录制中（退游/空闲后自动上云）";
                ToggleOrderBtn.Content = "结束接单";
                ToggleOrderBtn.Background = new SolidColorBrush(MediaColor.FromRgb(218, 54, 51));
                PauseBtn.Content = "暂停";
                PauseBtn.IsEnabled = true;
                break;
            case RecordingState.Paused:
                StatusDot.Fill = new SolidColorBrush(MediaColor.FromRgb(210, 153, 34));
                StatusTitle.Text = "录制已暂停";
                ToggleOrderBtn.Content = "开始接单";
                ToggleOrderBtn.Background = new SolidColorBrush(MediaColor.FromRgb(35, 134, 54));
                PauseBtn.Content = "恢复";
                PauseBtn.IsEnabled = true;
                break;
        }
    }

    private async void ToggleOrderBtn_Click(object sender, RoutedEventArgs e)
    {
        ToggleOrderBtn.IsEnabled = false;
        try
        {
            if (_coordinator.State == RecordingState.RecordingOrder)
            {
                await _coordinator.EndOrderAsync();
            }
            else
            {
                await _coordinator.StartOrderAsync(OrderRefBox.Text, NoteBox.Text);
            }
            RefreshUi();
        }
        finally
        {
            ToggleOrderBtn.IsEnabled = true;
        }
    }

    private async void PauseBtn_Click(object sender, RoutedEventArgs e)
    {
        PauseBtn.IsEnabled = false;
        try
        {
            if (_coordinator.State == RecordingState.Paused)
            {
                await _coordinator.ResumeAsync();
            }
            else
            {
                await _coordinator.PauseAsync();
            }
            RefreshUi();
        }
        finally
        {
            PauseBtn.IsEnabled = true;
        }
    }

    private void BackfillBtn_Click(object sender, RoutedEventArgs e)
    {
        var win = new BackfillWindow(_coordinator, _store) { Owner = this };
        win.ShowDialog();
    }
}
