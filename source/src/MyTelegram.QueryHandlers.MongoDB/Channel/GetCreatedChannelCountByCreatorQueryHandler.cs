namespace MyTelegram.QueryHandlers.MongoDB.Channel;

public class GetCreatedChannelCountByCreatorQueryHandler(IQueryOnlyReadModelStore<ChannelReadModel> store)
    : IQueryHandler<GetCreatedChannelCountByCreatorQuery, int>
{
    public async Task<int> ExecuteQueryAsync(GetCreatedChannelCountByCreatorQuery query, CancellationToken cancellationToken)
    {
        return (int)await store.CountAsync(
            p => p.CreatorId == query.CreatorUserId && p.Broadcast == query.Broadcast && !p.IsDeleted,
            cancellationToken);
    }
}
