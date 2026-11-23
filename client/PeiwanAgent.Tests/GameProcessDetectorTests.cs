using PeiwanAgent.Core;
using PeiwanAgent.Core.Detection;

namespace PeiwanAgent.Tests;

public class GameProcessDetectorTests
{
    private sealed class FakeProbe : IProcessProbe
    {
        public List<string> Names { get; } = new();
        public IReadOnlyCollection<string> GetRunningProcessNames() => Names.ToArray();
    }

    [Fact]
    public void DefaultTargetIsDeltaForce()
        => Assert.Equal("DeltaForceClient-Win64-Shipping.exe", AgentOptions.DefaultGameProcessName);

    [Fact]
    public void FiresStartedThenExitedOnce()
    {
        var p = new FakeProbe();
        var d = new GameProcessDetector(p);
        int started = 0, exited = 0;
        d.GameStarted += (_, _) => started++;
        d.GameExited += (_, _) => exited++;

        Assert.False(d.Poll());
        p.Names.Add("DeltaForceClient-Win64-Shipping.exe");
        Assert.True(d.Poll());
        Assert.False(d.Poll());
        Assert.True(d.IsGameRunning);
        p.Names.Clear();
        Assert.True(d.Poll());
        Assert.False(d.Poll());

        Assert.Equal(1, started);
        Assert.Equal(1, exited);
        Assert.False(d.IsGameRunning);
    }

    [Theory]
    [InlineData("deltaforceclient-win64-shipping.exe")]
    [InlineData("DeltaForceClient-Win64-Shipping")]
    public void MatchesCaseInsensitiveWithOrWithoutExe(string name)
    {
        var p = new FakeProbe();
        p.Names.Add(name);
        var d = new GameProcessDetector(p);
        Assert.True(d.Poll());
        Assert.True(d.IsGameRunning);
    }

    [Fact]
    public void IgnoresOtherProcesses()
    {
        var p = new FakeProbe();
        p.Names.AddRange(new[] { "notepad.exe", "chrome" });
        var d = new GameProcessDetector(p);
        Assert.False(d.Poll());
        Assert.False(d.IsGameRunning);
    }

    [Fact]
    public void CustomTargetsSupported()
    {
        var p = new FakeProbe();
        p.Names.Add("game2.exe");
        var d = new GameProcessDetector(p, new[] { "game2.exe" });
        Assert.True(d.Poll());
    }

    [Fact]
    public void EmptyTargetsRejected()
        => Assert.Throws<ArgumentException>(() => new GameProcessDetector(new FakeProbe(), Array.Empty<string>()));
}