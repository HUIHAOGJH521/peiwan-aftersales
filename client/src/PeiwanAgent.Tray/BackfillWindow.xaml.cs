using System.IO;
using System.Windows;
using PeiwanAgent.Core.Models;
using PeiwanAgent.Core.Services;
using PeiwanAgent.Core.Storage;
using WpfMessageBox = System.Windows.MessageBox;

namespace PeiwanAgent.Tray;

public partial class BackfillWindow : Window
{
    private readonly RecordingCoordinator _coordinator;
    private readonly LocalStore _store;
    private IReadOnlyList<MediaSegmentRecord> _items = [];

    public BackfillWindow(RecordingCoordinator coordinator, LocalStore store)
    {
        InitializeComponent();
        _coordinator = coordinator;
        _store = store;
        LoadSegments();
    }

    private void LoadSegments()
    {
        _items = _store.GetRecentPrivateSegments(DateTimeOffset.UtcNow);
        SegmentList.Items.Clear();
        foreach (var seg in _items)
        {
            var localStart = seg.StartedAt.LocalDateTime.ToString("MM-dd HH:mm:ss");
            var durSec = Math.Max(1, seg.DurationMs / 1000);
            var sizeMb = seg.FileSize / (1024.0 * 1024.0);
            SegmentList.Items.Add($"[{localStart}] 时长 {durSec}s | {sizeMb:F1} MB | {Path.GetFileName(seg.FilePath)}");
        }
    }

    private void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (ConsentCheck.IsChecked != true)
        {
            WpfMessageBox.Show(this, "请先勾选授权确认框。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedIds = new List<string>();
        foreach (var item in SegmentList.SelectedItems)
        {
            var idx = SegmentList.Items.IndexOf(item);
            if (idx >= 0 && idx < _items.Count)
            {
                selectedIds.Add(_items[idx].SegmentId);
            }
        }

        if (selectedIds.Count == 0)
        {
            WpfMessageBox.Show(this, "请至少选择一个本地片段。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _coordinator.RequestManualBackfill(selectedIds, OrderRefInput.Text, NoteInput.Text);
        WpfMessageBox.Show(this, $"已将 {selectedIds.Count} 个片段加入后台补传队列。", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
