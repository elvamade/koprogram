namespace MyTelegram.Messenger.Handlers.LatestLayer.Help;
/// <summary>
/// Get app-specific configuration, see <a href="https://corefork.telegram.org/api/config#client-configuration">client configuration</a> for more info on the result.
/// <para><c>See <a href="https://corefork.telegram.org/method/help.getAppConfig"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✔]
/// </remarks>
internal sealed class GetAppConfigHandler(IAppConfigHelper appConfigHelper, IUserAppService userAppService) : RpcResultObjectHandler<Schema.Help.RequestGetAppConfig, Schema.Help.IAppConfig>
{
    private const int FrozenDurationSeconds = 30 * 24 * 60 * 60;
    private const string FrozenAppealUrl = "https://t.me/SpamBot?start=MONTH_ERR";

    protected override async Task<Schema.Help.IAppConfig> HandleCoreAsync(IRequestInput input, Schema.Help.RequestGetAppConfig obj)
    {
        var config = appConfigHelper.GetAppConfig();
        var hash = appConfigHelper.GetAppConfigHash();

        if (input.UserId > 0)
        {
            var userReadModel = await userAppService.GetAsync(input.UserId);
            if (userReadModel.Frozen)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var freezeSinceDate = (int)Math.Max(1, now - 60);
                var freezeUntilDate = (int)Math.Min(int.MaxValue, now + FrozenDurationSeconds);
                config = OverrideFrozenConfig(config, freezeSinceDate, freezeUntilDate, FrozenAppealUrl);
                hash = ComputeAppConfigHash(config);
            }
        }

        if (obj.Hash == hash)
        {
            return new TAppConfigNotModified();
        }

        var appConfig = new TAppConfig
        {
            Config = config,
            Hash = hash
        };
        return appConfig;
    }

    private static IJSONValue OverrideFrozenConfig(IJSONValue config, int freezeSinceDate, int freezeUntilDate, string freezeAppealUrl)
    {
        if (config is not TJsonObject jsonObject)
        {
            return config;
        }

        var values = jsonObject.Value.ToList();
        SetOrReplace(values, "freeze_since_date", new TJsonNumber { Value = freezeSinceDate });
        SetOrReplace(values, "freeze_until_date", new TJsonNumber { Value = freezeUntilDate });
        SetOrReplace(values, "freeze_appeal_url", new TJsonString { Value = freezeAppealUrl });

        return new TJsonObject
        {
            Value = [.. values]
        };
    }

    private static void SetOrReplace(List<IJSONObjectValue> values, string key, IJSONValue value)
    {
        for (var i = values.Count - 1; i >= 0; i--)
        {
            if (values[i] is TJsonObjectValue oldValue && string.Equals(oldValue.Key, key, StringComparison.Ordinal))
            {
                values.RemoveAt(i);
            }
        }

        values.Add(new TJsonObjectValue
        {
            Key = key,
            Value = value
        });
    }

    private static int ComputeAppConfigHash(IJSONValue appConfig)
    {
        var bytes = appConfig.ToBytes();
        var digest = System.Security.Cryptography.SHA256.HashData(bytes);
        return BitConverter.ToInt32(digest, 0);
    }
}
