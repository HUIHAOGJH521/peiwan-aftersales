using PeiwanAgent.Core.Storage;

namespace PeiwanAgent.Core.Services;

public sealed class RetentionCleaner
{
    private readonly LocalStore _store;

    public RetentionCleaner(LocalStore store)
    {
        _store = store;
    }

    public int CleanExpired(DateTimeOffset? now = null)
    {
        var currentTime = now ?? DateTimeOffset.UtcNow;
        var expired = _store.GetExpiredSegments(currentTime);
        var deletedCount = 0;

        foreach (var seg in expired)
        {
            try
            {
                if (File.Exists(seg.FilePath))
                {
                    File.Delete(seg.FilePath);
                }

                // 若为降级模式目录，同步清理截图目录
                var parentDir = Path.GetDirectoryName(seg.FilePath);
                if (!string.IsNullOrWhiteSpace(parentDir) && seg.CodecMode == "audio_screenshots")
                {
                    var framesDir = Path.Combine(parentDir, $"{seg.SegmentId}_frames");
                    if (Directory.Exists(framesDir))
                    {
                        Directory.Delete(framesDir, recursive: true);
                    }
                }
            }
            catch
            {
                // 若文件暂被占用，跳过待下轮清理
                continue;
            }

            _store.DeleteSegment(seg.SegmentId);
            deletedCount++;
        }

        return deletedCount;
    }
}
