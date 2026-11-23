using PeiwanAgent.Core.Retention;

namespace PeiwanAgent.Tests;

public class RetentionPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExactlyThreeDaysIsExpired_JustUnderIsNot()
    {
        Assert.True(RetentionPolicy.IsExpired(Now.AddDays(-3), Now));
        Assert.False(RetentionPolicy.IsExpired(Now.AddDays(-3).AddSeconds(1), Now));
    }

    [Fact]
    public void FutureTimestampNotExpired()
        => Assert.False(RetentionPolicy.IsExpired(Now.AddHours(1), Now));

    [Fact]
    public void SelectExpiredFiltersAndKeepsOrder()
    {
        var items = new[]
        {
            ("old1", Now.AddDays(-10)), ("fresh", Now.AddHours(-1)),
            ("old2", Now.AddDays(-4)), ("edge", Now.AddDays(-3)),
        };
        var r = RetentionPolicy.SelectExpired(items, i => i.Item2, Now);
        Assert.Equal(new[] { "old1", "old2", "edge" }, r.Select(i => i.Item1));
    }

    [Fact]
    public void CustomDaysAndDoesNotMutateInput()
    {
        var items = new List<DateTimeOffset> { Now.AddDays(-2), Now.AddDays(-0.5) };
        var r = RetentionPolicy.SelectExpired(items, x => x, Now, retentionDays: 1);
        Assert.Single(r);
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public void NegativeDaysRejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => RetentionPolicy.IsExpired(Now, Now, -1));
}