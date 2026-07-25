namespace MyTelegram.Messenger.Services.Impl;

public class UserAppService(IQueryProcessor queryProcessor,
    IReadModelCacheHelper<IUserReadModel> userReadModelCacheHelper,
    IReadModelCacheHelper<IUserFullReadModel> userFullReadModelCacheHelper) : ReadModelWithCacheAppService<IUserReadModel>(userReadModelCacheHelper), IUserAppService, ITransientDependency
{
    public async Task CheckAccountPremiumStatusAsync(long userId)
    {
        var userReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(userId));
        if (userReadModel == null)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        if (!userReadModel!.Premium)
        {
            RpcErrors.RpcErrors400.PremiumAccountRequired.ThrowRpcError();
        }
    }


    public Task<IUserFullReadModel?> GetUserFullAsync(long userId)
    {
        return userFullReadModelCacheHelper.GetOrCreateAsync(userId,
            () => queryProcessor.ProcessAsync(new GetUserFullQuery(userId)), p => p.Id);
    }

    protected override async Task<IUserReadModel?> GetReadModelAsync(long id)
    {
        var userReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(id));

        if (userReadModel == null)
        {
            return await CreateNonExistsReadModelAsync(id);
        }

        return userReadModel;
    }

    protected override string GetReadModelId(IUserReadModel readModel) => readModel.Id;

    protected override long GetReadModelInt64Id(IUserReadModel readModel) => readModel.UserId;

    protected override bool IsValidReadModel(IUserReadModel readModel)
    {
        // A synthetic "deleted" placeholder can be cached before the real user is created.
        // Treat it as invalid so the store is queried again and fresh data is loaded.
        return readModel.IsDeleted != true;
    }

    protected override Task<IUserReadModel?> CreateNonExistsReadModelAsync(long id)
    {
        return Task.FromResult<IUserReadModel?>(new UserReadModel { UserId = id, IsDeleted = true, Id = UserId.Create(id).Value });
    }

    protected override Task<IReadOnlyCollection<IUserReadModel>> GetReadModelListAsync(List<long> ids)
    {
        return queryProcessor.ProcessAsync(new GetUsersByUserIdListQuery(ids));
    }
}
