namespace MyTelegram.Messenger.Services.Impl;

public class ActionRateLimitService(ICacheManager<ActionRateLimitCacheItem> cacheManager)
    : IActionRateLimitService, ITransientDependency
{
    public async Task<int> CheckAndIncrementAsync(string key, int maxCount, int windowSeconds, int incrementBy = 1)
    {
        if (maxCount <= 0 || windowSeconds <= 0 || incrementBy <= 0)
        {
            return 0;
        }

        var now = DateTime.UtcNow.ToTimestamp();
        var cacheItem = await cacheManager.GetAsync(key);
        if (cacheItem == null || cacheItem.ExpiresAt <= now)
        {
            cacheItem = new ActionRateLimitCacheItem(0, now + windowSeconds);
        }

        if (cacheItem.Count + incrementBy > maxCount)
        {
            return Math.Max(1, cacheItem.ExpiresAt - now);
        }

        var newCacheItem = cacheItem with { Count = cacheItem.Count + incrementBy };
        var ttl = Math.Max(1, newCacheItem.ExpiresAt - now);
        await cacheManager.SetAsync(key, newCacheItem, ttl);

        return 0;
    }
}

public record ActionRateLimitCacheItem(int Count, int ExpiresAt);
