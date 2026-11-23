namespace PeiwanAgent.Core.Retention;

/// <summary>本地保留清理纯函数(默认 3 天 = 72h)。不做任何 IO。</summary>
public static class RetentionPolicy
{
    public const int DefaultRetentionDays = 3;

    public static bool IsExpired(DateTimeOffset createdAt, DateTimeOffset now, int retentionDays = DefaultRetentionDays)
    {
        if (retentionDays < 0) throw new ArgumentOutOfRangeException(nameof(retentionDays));
        return now - createdAt >= TimeSpan.FromDays(retentionDays);
    }

    public static IReadOnlyList<T> SelectExpired<T>(
        IEnumerable<T> items,
        Func<T, DateTimeOffset> createdAtSelector,
        DateTimeOffset now,
        int retentionDays = DefaultRetentionDays)
        => items.Where(i => IsExpired(createdAtSelector(i), now, retentionDays)).ToList();
}