namespace MyTelegram.Messenger.Services.Impl;
public partial class AppConfigHelper : IAppConfigHelper, ISingletonDependency
{
    public AppConfigHelper()
    {
        GetAppConfig();
        if (_jsonValue != null)
        {
            _hash = ComputeAppConfigHash(_jsonValue);
        }
    }

    public int GetAppConfigHash()
    {
        return _hash;
    }

    private void AddCustomConfig()
    {
        SetConfig("stargifts_blocked", new TJsonBool { Value = false });
        SetConfig("stars_purchase_blocked", new TJsonBool { Value = false });
        SetConfig("stars_gifts_enabled", new TJsonBool { Value = true });
        SetConfig("premium_purchase_blocked", new TJsonBool { Value = false });
        SetConfig("stars_paid_messages_available", new TJsonBool { Value = true });
        SetConfig("stars_paid_message_amount_max", new TJsonNumber { Value = 10000 });
        SetConfig("hidden_members_group_size_min", new TJsonNumber { Value = 10 });
        SetConfig("freeze_appeal_url", new TJsonString { Value = string.Empty });
    }

    private void SetConfig(string key, IJSONValue value)
    {
        var oldValue = _jsonValue?.Value.FirstOrDefault(p => p.Key == key);
        if (oldValue != null)
        {
            _jsonValue?.Value.Remove(oldValue);
        }
        _jsonValue?.Value.Add(new TJsonObjectValue
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
