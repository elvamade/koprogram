namespace MyTelegram.Messenger.Services.Interfaces;

public interface IActionRateLimitService
{
    Task<int> CheckAndIncrementAsync(string key, int maxCount, int windowSeconds, int incrementBy = 1);
}
