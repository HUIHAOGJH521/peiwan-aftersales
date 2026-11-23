using PeiwanAgent.Core.Orders;

namespace PeiwanAgent.Tests;

public class OrderStateMachineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 14, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void StartsInPrivate()
    {
        var m = new OrderStateMachine();
        Assert.Equal(RecordingMode.Private, m.Mode);
    }

    [Fact]
    public void StartOrderRaisesSplitWithOrderRefAndNote()
    {
        var m = new OrderStateMachine();
        var events = new List<SplitRequestedEventArgs>();
        m.SplitRequested += (_, e) => events.Add(e);

        Assert.True(m.StartOrder(T0, " BX001 ", "老板很急"));

        var e = Assert.Single(events);
        Assert.Equal(RecordingMode.Private, e.PreviousMode);
        Assert.Equal(RecordingMode.Order, e.NewMode);
        Assert.Equal("BX001", e.OrderRef);
        Assert.Equal("老板很急", e.Note);
        Assert.False(string.IsNullOrEmpty(e.SessionId));
        Assert.Equal(T0, e.At);
        Assert.Equal(RecordingMode.Order, m.Mode);
    }

    [Fact]
    public void StartOrderWithoutOrderRefOrNoteIsAllowed()
    {
        var m = new OrderStateMachine();
        Assert.True(m.StartOrder(T0, "  ", null));
        Assert.Null(m.CurrentOrderRef);
        Assert.Null(m.CurrentNote);
    }

    [Fact]
    public void EndOrderRaisesSplitCarryingEndedSession()
    {
        var m = new OrderStateMachine();
        m.StartOrder(T0, "BX1", "n", "sess-1");
        var events = new List<SplitRequestedEventArgs>();
        m.SplitRequested += (_, e) => events.Add(e);

        Assert.True(m.EndOrder(T0.AddHours(1)));

        var e = Assert.Single(events);
        Assert.Equal(RecordingMode.Order, e.PreviousMode);
        Assert.Equal(RecordingMode.Private, e.NewMode);
        Assert.Equal("sess-1", e.SessionId);
        Assert.Equal("BX1", e.OrderRef);
        Assert.Equal(RecordingMode.Private, m.Mode);
        Assert.Null(m.CurrentSessionId);
    }

    [Fact]
    public void RepeatedStartOrEndDoesNotSplit()
    {
        var m = new OrderStateMachine();
        int n = 0;
        m.SplitRequested += (_, _) => n++;
        Assert.False(m.EndOrder(T0));
        Assert.True(m.StartOrder(T0));
        Assert.False(m.StartOrder(T0.AddMinutes(1), "other"));
        Assert.Equal(1, n);
    }

    [Fact]
    public void NewOrderGetsNewSessionId()
    {
        var m = new OrderStateMachine();
        m.StartOrder(T0);
        var first = m.CurrentSessionId;
        m.EndOrder(T0.AddMinutes(5));
        m.StartOrder(T0.AddMinutes(6));
        Assert.NotEqual(first, m.CurrentSessionId);
    }
}