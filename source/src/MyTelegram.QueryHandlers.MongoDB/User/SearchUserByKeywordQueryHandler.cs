namespace MyTelegram.QueryHandlers.MongoDB.User;

public class SearchUserByKeywordQueryHandler(IQueryOnlyReadModelStore<UserReadModel> store) :
    IQueryHandler<SearchUserByKeywordQuery, IReadOnlyCollection<IUserReadModel>>
{
    public async Task<IReadOnlyCollection<IUserReadModel>> ExecuteQueryAsync(SearchUserByKeywordQuery query,
        CancellationToken cancellationToken)
    {
        var q = query.Keyword;
        if (!string.IsNullOrEmpty(q) && q.StartsWith('@'))
        {
            q = query.Keyword[1..];
        }

        q = q?.ToLower();
        var limit = query.Limit > 0 ? query.Limit : 50;

        Expression<Func<UserReadModel, bool>> predicate = x => true;
        predicate = predicate.WhereIf(!string.IsNullOrEmpty(q),
            p => (p.UserName != null && p.UserName.ToLower().StartsWith(q)) ||
                 p.FirstName.ToLower().Contains(q) ||
                 (p.LastName != null && p.LastName.ToLower().StartsWith(q))
                 );

        return await store.FindAsync(predicate, 0, limit, new SortOptions<UserReadModel>(p => p.FirstName, SortType.Ascending), cancellationToken);
    }
}
