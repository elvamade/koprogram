namespace MyTelegram.QueryHandlers.InMemory.UserName;

public class SearchUserNameQueryHandler(IQueryOnlyReadModelStore<UserNameReadModel> store) : IQueryHandler<SearchUserNameQuery, IReadOnlyCollection<IUserNameReadModel>>
{
    public async Task<IReadOnlyCollection<IUserNameReadModel>> ExecuteQueryAsync(SearchUserNameQuery query,
        CancellationToken cancellationToken)
    {
        var keyword = query.Keyword.ToLower();
        return await store.FindAsync(p => p.UserName.ToLower().StartsWith(keyword),
            limit: 50, cancellationToken: cancellationToken);
    }
}
