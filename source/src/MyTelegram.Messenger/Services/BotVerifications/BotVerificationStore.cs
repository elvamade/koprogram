using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.BotVerifications;

internal static class BotVerificationStore
{
    internal const string VerifierSettingsCollectionName = "bot_verifier_settings";
    internal const string UserReadModelCollectionName = "eventflow-userreadmodel";
    internal const string ChannelReadModelCollectionName = "eventflow-channelreadmodel";

    internal static IMongoCollection<BsonDocument> GetVerifierSettingsCollection(IMongoDatabase database)
        => database.GetCollection<BsonDocument>(VerifierSettingsCollectionName);

    internal static IMongoCollection<BsonDocument> GetUserReadModelCollection(IMongoDatabase database)
        => database.GetCollection<BsonDocument>(UserReadModelCollectionName);

    internal static IMongoCollection<BsonDocument> GetChannelReadModelCollection(IMongoDatabase database)
        => database.GetCollection<BsonDocument>(ChannelReadModelCollectionName);

    internal static async Task<BsonDocument?> GetVerifierSettingsAsync(IMongoDatabase database, long botId)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("BotId", botId);
        return await GetVerifierSettingsCollection(database).Find(filter).FirstOrDefaultAsync();
    }

    internal static async Task<IBotVerification?> GetBotVerificationAsync(IMongoDatabase database, PeerType peerType, long peerId)
    {
        var verifierSettings = await GetVerifierSettingsForPeerAsync(database, peerType, peerId);
        if (verifierSettings == null)
        {
            return null;
        }

        return BuildBotVerification(verifierSettings);
    }

    internal static async Task<BsonDocument?> GetVerifierSettingsForPeerAsync(IMongoDatabase database, PeerType peerType, long peerId, long? excludeBotId = null)
    {
        FilterDefinition<BsonDocument> filter = peerType switch
        {
            PeerType.User => Builders<BsonDocument>.Filter.Or(
                Builders<BsonDocument>.Filter.AnyEq("UserIds", peerId),
                Builders<BsonDocument>.Filter.Eq("UserId", peerId)
            ),
            PeerType.Channel => Builders<BsonDocument>.Filter.Or(
                Builders<BsonDocument>.Filter.AnyEq("ChannelIds", peerId),
                Builders<BsonDocument>.Filter.Eq("ChannelId", peerId)
            ),
            _ => Builders<BsonDocument>.Filter.Empty
        };

        if (excludeBotId.HasValue)
        {
            filter = Builders<BsonDocument>.Filter.And(
                filter,
                Builders<BsonDocument>.Filter.Ne("BotId", excludeBotId.Value)
            );
        }

        if (filter == Builders<BsonDocument>.Filter.Empty)
        {
            return null;
        }

        return await GetVerifierSettingsCollection(database).Find(filter).FirstOrDefaultAsync();
    }

    internal static IBotVerification? BuildBotVerification(BsonDocument verifierSettingsDoc)
    {
        var botId = GetLong(verifierSettingsDoc, "BotId");
        if (botId == 0)
        {
            botId = GetLong(verifierSettingsDoc, "bot_id");
        }
        var icon = GetIconFromVerifierSettings(verifierSettingsDoc);
        var company = GetStringFromAny(verifierSettingsDoc, "Company", "company", "Organization", "organization");
        var customDescription = GetStringFromAny(verifierSettingsDoc, "CustomDescription", "custom_description");
        var description = GetStringFromAny(verifierSettingsDoc, "Description", "description");
        var resolvedDescription = !string.IsNullOrWhiteSpace(customDescription)
            ? customDescription
            : !string.IsNullOrWhiteSpace(description)
                ? description
                : !string.IsNullOrWhiteSpace(company)
                    ? BuildDefaultDescription(company!)
                    : null;

        if (botId == 0 || icon == 0 || string.IsNullOrWhiteSpace(resolvedDescription))
        {
            return null;
        }

        return new TBotVerification
        {
            BotId = botId,
            Icon = icon,
            Description = resolvedDescription!
        };
    }

    internal static long GetIconFromVerifierSettings(BsonDocument verifierSettingsDoc)
    {
        var icon = GetLong(verifierSettingsDoc, "Icon");
        if (icon == 0)
        {
            icon = GetLong(verifierSettingsDoc, "icon");
        }
        if (icon == 0)
        {
            icon = GetLong(verifierSettingsDoc, "StickerId");
        }
        if (icon == 0)
        {
            icon = GetLong(verifierSettingsDoc, "sticker_id");
        }

        return icon;
    }

    internal static bool? GetTextColorFromVerifierSettings(BsonDocument verifierSettingsDoc)
    {
        if (verifierSettingsDoc.Contains("TextColor") && !verifierSettingsDoc["TextColor"].IsBsonNull)
        {
            return verifierSettingsDoc["TextColor"].AsBoolean;
        }

        if (verifierSettingsDoc.Contains("text_color") && !verifierSettingsDoc["text_color"].IsBsonNull)
        {
            return verifierSettingsDoc["text_color"].AsBoolean;
        }

        return null;
    }

    internal static string BuildDefaultDescription(string company)
        => $"Was verified by organization \"{company}\"";

    internal static long GetLong(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull)
        {
            return 0;
        }

        return doc[field].IsInt64 ? doc[field].AsInt64 : doc[field].AsInt32;
    }

    internal static bool GetBool(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull)
        {
            return false;
        }

        return doc[field].AsBoolean;
    }

    internal static string? GetString(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull)
        {
            return null;
        }

        return doc[field].AsString;
    }

    internal static string? GetStringFromAny(BsonDocument doc, params string[] fields)
    {
        foreach (var field in fields)
        {
            var value = GetString(doc, field);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    internal static IReadOnlyCollection<long> GetLongArray(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull)
        {
            return Array.Empty<long>();
        }

        if (doc[field] is not BsonArray array)
        {
            return Array.Empty<long>();
        }

        var list = new List<long>(array.Count);
        foreach (var item in array)
        {
            if (item.IsInt64)
            {
                list.Add(item.AsInt64);
            }
            else if (item.IsInt32)
            {
                list.Add(item.AsInt32);
            }
        }

        return list;
    }
}
