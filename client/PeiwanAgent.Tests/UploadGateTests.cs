using PeiwanAgent.Core.Upload;

namespace PeiwanAgent.Tests;

public class UploadGateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 14, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan NoIdle = TimeSpan.Zero;

    [Fact]
    public void GameRunningAlwaysSuspended_EvenIfIdleIsHuge()
    {
        var g = new UploadGate(30, 60);
        g.NotifyGameStarted();
        Assert.Equal(GateDecision.SuspendedGameRunning, g.Evaluate(T0, TimeSpan.FromHours(5)));
        Assert.Equal(GateDecision.SuspendedGameRunning, g.Evaluate(T0.AddDays(1), NoIdle));
    }

    [Fact]
    public void AfterExitWaitsThenOpensByDelay()
    {
        var g = new UploadGate(30, 60);
        g.NotifyGameStarted();
        g.NotifyGameExited(T0);
        Assert.Equal(GateDecision.WaitingAfterExit, g.Evaluate(T0.AddSeconds(29), NoIdle));
        Assert.Equal(GateDecision.Open, g.Evaluate(T0.AddSeconds(30), NoIdle));
    }

    [Fact]
    public void AfterExitOpensEarlyWhenIdleReachesThreshold()
    {
        var g = new UploadGate(30, 60);
        g.NotifyGameStarted();
        g.NotifyGameExited(T0);
        Assert.Equal(GateDecision.WaitingAfterExit, g.Evaluate(T0.AddSeconds(5), TimeSpan.FromSeconds(59)));
        Assert.Equal(GateDecision.Open, g.Evaluate(T0.AddSeconds(5), TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void RestartingGameSuspendsAgain()
    {
        var g = new UploadGate(30, 60);
        g.NotifyGameStarted();
        g.NotifyGameExited(T0);
        Assert.Equal(GateDecision.Open, g.Evaluate(T0.AddMinutes(5), NoIdle));
        g.NotifyGameStarted();
        Assert.Equal(GateDecision.SuspendedGameRunning, g.Evaluate(T0.AddMinutes(6), NoIdle));
        g.NotifyGameExited(T0.AddMinutes(7));
        Assert.Equal(GateDecision.WaitingAfterExit, g.Evaluate(T0.AddMinutes(7), NoIdle));
    }

    [Fact]
    public void NeverSawGame_IsOpen()
        => Assert.Equal(GateDecision.Open, new UploadGate().Evaluate(T0, NoIdle));

    [Fact]
    public void PrivateSegmentsNeverEnqueued_EvenWhenGateOpen()
    {
        var g = new UploadGate();
        Assert.False(UploadGate.ShouldEnqueue(isOrderSegment: false));
        Assert.False(g.CanUpload(false, false, T0, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void ManualBackfillPrivateSegmentIsEnqueuedButStillGated()
    {
        var g = new UploadGate();
        Assert.True(UploadGate.ShouldEnqueue(false, isManualBackfill: true));
        g.NotifyGameStarted();
        Assert.False(g.CanUpload(false, true, T0, NoIdle));
        g.NotifyGameExited(T0);
        Assert.True(g.CanUpload(false, true, T0.AddMinutes(1), NoIdle));
    }

    [Fact]
    public void OrderSegmentUploadsOnlyWhenOpen()
    {
        var g = new UploadGate(30, 60);
        g.NotifyGameStarted();
        Assert.False(g.CanUpload(true, false, T0, NoIdle));
        g.NotifyGameExited(T0);
        Assert.False(g.CanUpload(true, false, T0.AddSeconds(1), NoIdle));
        Assert.True(g.CanUpload(true, false, T0.AddSeconds(31), NoIdle));
    }

    [Fact]
    public void NegativeConfigRejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new UploadGate(-1, 60));
}