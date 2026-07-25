using System.Collections.Concurrent;

namespace MyTelegram.Services.Services;

public class RequestHelper(
    IScheduleAppService scheduleAppService,
    IEventBus eventBus
) : IRequestHelper, ISingletonDependency
{
    private readonly ConcurrentDictionary<RequestDedupKey, byte> _requestMessageIds = [];
    private readonly int _duplicateRequestIntervalSeconds = 300;
    /// <summary>
    ///     Check for duplicate requests within 300 seconds
    /// </summary>
    /// <param name="requestInfo"></param>
    /// <param name="requestData"></param>
    /// <returns></returns>
    public async Task<bool> CheckRequestAsync(IRequestInput requestInfo, IObject requestData)
    {
        var key = GetRequestDedupKey(requestInfo, requestData);
        if (_requestMessageIds.ContainsKey(key))
        {
            await eventBus.PublishAsync(new DuplicateCommandEvent(requestInfo.PermAuthKeyId, requestInfo.UserId,
                requestInfo.ReqMsgId));

            return false;
        }

        if (_requestMessageIds.TryAdd(key, 0))
        {
            scheduleAppService.Execute(() =>
                {
                    _requestMessageIds.TryRemove(key, out _);
                },
                TimeSpan.FromSeconds(_duplicateRequestIntervalSeconds));

            return true;
        }

        await eventBus.PublishAsync(new DuplicateCommandEvent(requestInfo.PermAuthKeyId, requestInfo.UserId,
            requestInfo.ReqMsgId));

        return false;
    }

    private static RequestDedupKey GetRequestDedupKey(IRequestInput requestInfo, IObject requestData)
    {
        var objectId = GetRequestObjectId(requestData, requestInfo.ObjectId);
        var scopeKey = requestInfo.PermAuthKeyId;
        if (scopeKey != 0)
        {
            return new RequestDedupKey(scopeKey, requestInfo.ReqMsgId, objectId, null);
        }

        scopeKey = requestInfo.AuthKeyId;
        if (scopeKey != 0)
        {
            return new RequestDedupKey(scopeKey, requestInfo.ReqMsgId, objectId, null);
        }

        scopeKey = requestInfo.SessionId;
        if (scopeKey != 0)
        {
            return new RequestDedupKey(scopeKey, requestInfo.ReqMsgId, objectId, null);
        }

        return new RequestDedupKey(0, requestInfo.ReqMsgId, objectId, requestInfo.ConnectionId);
    }

    private static uint GetRequestObjectId(IObject requestData, uint defaultObjectId)
    {
        var current = requestData;
        while (current is IHasSubQuery subQuery)
        {
            current = subQuery.Query;
        }

        return current?.ConstructorId ?? defaultObjectId;
    }

    private readonly record struct RequestDedupKey(long ScopeKey, long ReqMsgId, uint ObjectId, string? ConnectionId);
}
