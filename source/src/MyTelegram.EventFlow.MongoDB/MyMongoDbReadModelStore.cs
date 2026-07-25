using EventFlow.Core;
using EventFlow.Core.RetryStrategies;
using EventFlow.Extensions;
using EventFlow.MongoDB.ReadStores;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using System.Linq.Expressions;

namespace MyTelegram.EventFlow.MongoDB;

public class MyMongoDbReadModelStore<TReadModel>(
    ILogger<MongoDbReadModelStore<TReadModel>> logger,
    IReadModelDescriptionProvider readModelDescriptionProvider,
    ITransientFaultHandler<IOptimisticConcurrencyRetryStrategy> transientFaultHandler,
    IMongoDbContext mongoDbContext)
    :
        MyMongoDbReadModelStore<TReadModel, IMongoDbContext>(logger, readModelDescriptionProvider,
            transientFaultHandler, mongoDbContext)
    where TReadModel : class, IMongoDbReadModel;

public class MyMongoDbReadModelStore<TReadModel, TDbContext>(
    ILogger<MongoDbReadModelStore<TReadModel>> logger,
    IReadModelDescriptionProvider readModelDescriptionProvider,
    ITransientFaultHandler<IOptimisticConcurrencyRetryStrategy> transientFaultHandler,
    TDbContext dbContext)
    : MongoDbReadModelStore<TReadModel>(logger, dbContext.GetDatabase(),
        readModelDescriptionProvider, transientFaultHandler), IMyMongoDbReadModelStore<TReadModel>
    where TReadModel : class, IMongoDbReadModel
    where TDbContext : IMongoDbContext
{
    private readonly ILogger<MongoDbReadModelStore<TReadModel>> _logger = logger;
    private readonly IReadModelDescriptionProvider _readModelDescriptionProvider = readModelDescriptionProvider;

    private IMongoDatabase GetDatabase() => dbContext.GetDatabase();

    public Task<IAggregateFluent<TResult>> AggregateAsync<TResult, TKey>(
        Expression<Func<TReadModel, bool>> filter,
        Expression<Func<TReadModel, TKey>> id,
        Expression<Func<IGrouping<TKey, TReadModel>, TResult>> group,
        AggregateOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var readModelDescription = _readModelDescriptionProvider.GetReadModelDescription<TReadModel>();
        var collection = GetDatabase().GetCollection<TReadModel>(readModelDescription.RootCollectionName.Value);

        return Task.FromResult(collection.Aggregate()
                .Match(filter)
                .Group(id, group))
            ;
    }

    public async Task<IAsyncCursor<TResult>> FindAsync<TResult>(Expression<Func<TReadModel, bool>> filter, FindOptions<TReadModel, TResult>? options = null, CancellationToken cancellationToken = default)
    {
        var readModelDescription = _readModelDescriptionProvider.GetReadModelDescription<TReadModel>();
        var collection = GetDatabase().GetCollection<TReadModel>(readModelDescription.RootCollectionName.Value);

        _logger.LogTrace(
            "Finding read model '{ReadModel}' with expression '{Filter}' from collection '{RootCollectionName}'",
            typeof(TReadModel).PrettyPrint(),
            filter,
            readModelDescription.RootCollectionName);

        return await collection.FindAsync(filter, options, cancellationToken);
    }

    public Task<long> CountAsync(Expression<Func<TReadModel, bool>>? filter = null, CancellationToken cancellationToken = default)
    {
        var readModelDescription = _readModelDescriptionProvider.GetReadModelDescription<TReadModel>();
        var collection = GetDatabase().GetCollection<TReadModel>(readModelDescription.RootCollectionName.Value);

        return collection.CountDocumentsAsync(filter, cancellationToken: cancellationToken);
    }
}
