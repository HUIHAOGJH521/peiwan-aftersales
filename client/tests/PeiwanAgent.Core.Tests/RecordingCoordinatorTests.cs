using PeiwanAgent.Core.Config;
using PeiwanAgent.Core.Models;
using PeiwanAgent.Core.Services;
using PeiwanAgent.Core.Storage;
using Xunit;

namespace PeiwanAgent.Core.Tests;

public sealed class RecordingCoordinatorTests : IDisposable
{
    private readonly string _tempDir;

    public RecordingCoordinatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"peiwan_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void ProcessMonitor_NormalizesExeSuffixAndCase()
    {
        var targets = new[] { "DeltaForceClient-Win64-Shipping.exe" };
        Assert.True(ProcessMonitor.IsMatch("deltaforceclient-win64-shipping", targets));
        Assert.True(ProcessMonitor.IsMatch("DeltaForceClient-Win64-Shipping.exe", targets));
        Assert.False(ProcessMonitor.IsMatch("notepad", targets));
    }

    [Fact]
    public async Task Coordinator_IsolatesPrivateAndOrderSegmentsOnToggle()
    {
        var config = new AgentConfig
        {
            DataRootDir = _tempDir,
            SegmentDurationSeconds = 300,
            LocalRetentionDays = 3
        };
        var store = new LocalStore(config.DatabasePath);
        var procMonitor = new FakeProcessMonitor();
        var probe = new FakeEncoderProbe();
        var worker = new FakeWorkerClient();

        await using var coordinator = new RecordingCoordinator(config, store, procMonitor, probe, worker);
        var t0 = new DateTimeOffset(2026, 11, 18, 20, 0, 0, TimeSpan.FromHours(8));

        // 1. 游戏未启动，保持 Idle
        await coordinator.TickAsync(t0);
        Assert.Equal(RecordingState.Idle, coordinator.State);

        // 2. 游戏启动，自动进入私人录制
        procMonitor.RunningGame = new DetectedProcessInfo(1001, "DeltaForceClient-Win64-Shipping", 1234, "Delta Force");
        await coordinator.TickAsync(t0.AddSeconds(5));
        Assert.Equal(RecordingState.RecordingPrivate, coordinator.State);
        Assert.Single(worker.Commands);
        Assert.Equal("START", worker.Commands[0].Cmd);
        Assert.False(worker.Commands[0].IsOrder);

        // 3. 点击开始接单 -> 立即 Split 封闭私人切片并开启接单切片
        await coordinator.StartOrderAsync("BX001", "绝密航天两把", t0.AddSeconds(60));
        Assert.Equal(RecordingState.RecordingOrder, coordinator.State);
        Assert.Equal(2, worker.Commands.Count);
        Assert.Equal("SPLIT", worker.Commands[1].Cmd);
        Assert.True(worker.Commands[1].IsOrder);
        Assert.NotNull(worker.Commands[1].SessionId);

        // 验证刚刚闭合的私人切片落在 LocalOnly，不进入上传队列
        var uploadQueueAfterStart = store.GetUploadQueue();
        Assert.Empty(uploadQueueAfterStart);
        var privateList = store.GetRecentPrivateSegments(t0.AddSeconds(61));
        Assert.Single(privateList);
        Assert.False(privateList[0].IsOrder);

        // 4. 点击结束接单 -> 立即 Split 封闭接单切片并回到私人录制
        await coordinator.EndOrderAsync(t0.AddSeconds(180));
        Assert.Equal(RecordingState.RecordingPrivate, coordinator.State);
        Assert.Equal(3, worker.Commands.Count);
        Assert.Equal("SPLIT", worker.Commands[2].Cmd);
        Assert.False(worker.Commands[2].IsOrder);

        // 验证闭合的接单切片已进入上传队列
        var uploadQueue = store.GetUploadQueue();
        Assert.Single(uploadQueue);
        Assert.True(uploadQueue[0].IsOrder);
        Assert.Equal(UploadStatus.Pending, uploadQueue[0].UploadStatus);

        // 5. 游戏退出 -> 停止录制回到 Idle
        procMonitor.RunningGame = null;
        await coordinator.TickAsync(t0.AddSeconds(240));
        Assert.Equal(RecordingState.Idle, coordinator.State);
        Assert.Equal("STOP", worker.Commands[^1].Cmd);
    }

    [Fact]
    public void RetentionCleaner_DeletesExpiredLocalAndCompletedSegmentsOnly()
    {
        var config = new AgentConfig { DataRootDir = _tempDir, LocalRetentionDays = 3 };
        var store = new LocalStore(config.DatabasePath);
        var cleaner = new RetentionCleaner(store);
        var now = new DateTimeOffset(2026, 11, 18, 20, 0, 0, TimeSpan.FromHours(8));

        var expiredFile = Path.Combine(_tempDir, "expired_private.mp4");
        var activeFile = Path.Combine(_tempDir, "fresh_private.mp4");
        var pendingExpiredFile = Path.Combine(_tempDir, "pending_order.mp4");
        File.WriteAllText(expiredFile, "dummy1");
        File.WriteAllText(activeFile, "dummy2");
        File.WriteAllText(pendingExpiredFile, "dummy3");

        store.InsertSegment(new MediaSegmentRecord(
            "seg-expired", null, 0, false, false, "video_hw", expiredFile, 6, "sha", 1000,
            UploadStatus.LocalOnly, null, now.AddDays(-4), now.AddDays(-4), now.AddDays(-1)));

        store.InsertSegment(new MediaSegmentRecord(
            "seg-fresh", null, 0, false, false, "video_hw", activeFile, 6, "sha", 1000,
            UploadStatus.LocalOnly, null, now.AddHours(-2), now.AddHours(-2), now.AddDays(2)));

        // 未上传完成的接单切片即使过 3 天也不能误删
        store.InsertSegment(new MediaSegmentRecord(
            "seg-pending", "sess-1", 1, true, false, "video_hw", pendingExpiredFile, 6, "sha", 1000,
            UploadStatus.Pending, null, now.AddDays(-4), now.AddDays(-4), now.AddDays(-1)));

        var removed = cleaner.CleanExpired(now);
        Assert.Equal(1, removed);
        Assert.False(File.Exists(expiredFile));
        Assert.True(File.Exists(activeFile));
        Assert.True(File.Exists(pendingExpiredFile));
    }

    [Fact]
    public void LocalStore_ManualBackfillMovesPrivateSegmentToPendingQueue()
    {
        var config = new AgentConfig { DataRootDir = _tempDir };
        var store = new LocalStore(config.DatabasePath);
        var now = DateTimeOffset.UtcNow;

        store.InsertSegment(new MediaSegmentRecord(
            "seg-p1", null, 0, false, false, "video_hw", "p1.mp4", 1024, "sha", 60000,
            UploadStatus.LocalOnly, null, now.AddMinutes(-10), now.AddMinutes(-5), now.AddDays(3)));

        Assert.Empty(store.GetUploadQueue());

        store.MarkSegmentsForManualBackfill(["seg-p1"], "sess-backfill", "BX999", "漏点接单补传", now);

        var queue = store.GetUploadQueue();
        Assert.Single(queue);
        Assert.True(queue[0].IsOrder);
        Assert.True(queue[0].IsManualBackfill);
        Assert.Equal("sess-backfill", queue[0].SessionId);
        Assert.Equal(UploadStatus.Pending, queue[0].UploadStatus);
    }

    private sealed class FakeProcessMonitor : IProcessMonitor
    {
        public DetectedProcessInfo? RunningGame { get; set; }
        public DetectedProcessInfo? FindRunningGame(IReadOnlyCollection<string> targetProcessNames) => RunningGame;
    }

    private sealed class FakeEncoderProbe : IEncoderProbe
    {
        public Task<EncoderProbeResult> ProbeBestEncoderAsync(string ffmpegPath, CancellationToken ct = default) =>
            Task.FromResult(new EncoderProbeResult(CodecMode.VideoHw, "h264_nvenc"));
    }

    private sealed class FakeWorkerClient : IWorkerClient
    {
        public List<WorkerCommand> Commands { get; } = [];
        private WorkerCommand? _active;
        private DateTimeOffset _activeStart = DateTimeOffset.UtcNow;

        public event Action<WorkerEvent>? EventReceived;
        public bool IsRunning => _active != null;

        public Task StartRecordingAsync(WorkerCommand command, CancellationToken ct = default)
        {
            Commands.Add(command);
            _active = command;
            _activeStart = DateTimeOffset.UtcNow;
            return Task.CompletedTask;
        }

        public Task SplitSegmentAsync(WorkerCommand command, CancellationToken ct = default)
        {
            Commands.Add(command);
            if (_active != null)
            {
                CloseCurrent();
            }
            _active = command;
            _activeStart = DateTimeOffset.UtcNow;
            return Task.CompletedTask;
        }

        public Task StopRecordingAsync(CancellationToken ct = default)
        {
            Commands.Add(new WorkerCommand("STOP"));
            if (_active != null)
            {
                CloseCurrent();
                _active = null;
            }
            return Task.CompletedTask;
        }

        private void CloseCurrent()
        {
            if (_active == null) return;
            EventReceived?.Invoke(new WorkerEvent(
                Event: "SEGMENT_CLOSED",
                SegmentId: _active.SegmentId,
                SessionId: _active.SessionId,
                SeqNo: _active.SeqNo,
                IsOrder: _active.IsOrder,
                CodecMode: _active.CodecMode,
                FilePath: _active.OutputPath,
                FileSize: 123456,
                Sha256: "abc",
                DurationMs: 5000,
                StartedAt: _activeStart,
                EndedAt: DateTimeOffset.UtcNow));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
