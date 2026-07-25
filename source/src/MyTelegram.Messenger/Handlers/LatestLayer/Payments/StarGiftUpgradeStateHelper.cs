using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal static class StarGiftUpgradeStateHelper
{
    internal static bool IsUpgradableGift(BsonDocument giftDoc)
    {
        return giftDoc.Contains("UpgradeStars") && !giftDoc["UpgradeStars"].IsBsonNull;
    }

    internal static bool IsUpgradeAlreadyPrepaid(BsonDocument savedGiftDoc)
    {
        if (savedGiftDoc.GetValue("PrepaidUpgrade", false).AsBoolean)
        {
            return true;
        }

        if (savedGiftDoc.GetValue("UpgradeSeparate", false).AsBoolean)
        {
            return true;
        }

        if (!savedGiftDoc.Contains("PrepaidUpgradeHash"))
        {
            return false;
        }

        var prepaidHashValue = savedGiftDoc["PrepaidUpgradeHash"];
        if (prepaidHashValue.IsBsonNull)
        {
            return false;
        }

        if (prepaidHashValue.BsonType != BsonType.String)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(prepaidHashValue.AsString);
    }

    internal static async Task SyncCanUpgradeAsync(
        IMongoCollection<BsonDocument> savedGiftsCollection,
        BsonDocument savedGiftDoc,
        BsonDocument giftDoc)
    {
        var isUpgraded = savedGiftDoc.GetValue("Upgraded", false).AsBoolean;
        var isConverted = savedGiftDoc.GetValue("Converted", false).AsBoolean;
        if (isUpgraded || isConverted)
        {
            return;
        }

        var canUpgrade = IsUpgradableGift(giftDoc);
        var currentCanUpgrade = savedGiftDoc.GetValue("CanUpgrade", false).AsBoolean;
        if (currentCanUpgrade == canUpgrade)
        {
            return;
        }

        savedGiftDoc["CanUpgrade"] = canUpgrade;
        var filter = BuildSavedGiftFilter(savedGiftDoc);
        await savedGiftsCollection.UpdateOneAsync(filter, Builders<BsonDocument>.Update.Set("CanUpgrade", canUpgrade));
    }

    private static FilterDefinition<BsonDocument> BuildSavedGiftFilter(BsonDocument savedGiftDoc)
    {
        if (savedGiftDoc.Contains("_id"))
        {
            return Builders<BsonDocument>.Filter.Eq("_id", savedGiftDoc["_id"]);
        }

        var ownerUserId = GetLong(savedGiftDoc, "OwnerUserId");
        var savedId = GetNullableLong(savedGiftDoc, "SavedId");
        if (savedId.HasValue)
        {
            return Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("OwnerUserId", ownerUserId),
                Builders<BsonDocument>.Filter.Eq("SavedId", savedId.Value)
            );
        }

        var msgId = GetNullableInt(savedGiftDoc, "MsgId");
        if (msgId.HasValue)
        {
            return Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("OwnerUserId", ownerUserId),
                Builders<BsonDocument>.Filter.Eq("MsgId", msgId.Value)
            );
        }

        return Builders<BsonDocument>.Filter.Eq("OwnerUserId", ownerUserId);
    }

    private static int? GetNullableInt(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull) return null;
        return doc[field].IsInt32 ? doc[field].AsInt32 : (int)doc[field].AsInt64;
    }

    private static long? GetNullableLong(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull) return null;
        return doc[field].IsInt64 ? doc[field].AsInt64 : doc[field].AsInt32;
    }

    private static long GetLong(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull) return 0;
        var value = doc[field];
        return value.IsInt64 ? value.AsInt64 : value.AsInt32;
    }
}
