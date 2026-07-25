using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal static class StarGiftResaleHelper
{
    public const string ResaleStarsAmountField = "ResaleStarsAmount";
    public const string ResaleStarsNanosField = "ResaleStarsNanos";
    public const string ResaleUpdatedAtField = "ResaleUpdatedAt";

    public static bool TryGetResaleStarsAmount(BsonDocument doc, out long amount, out int nanos)
    {
        amount = 0;
        nanos = 0;

        if (!doc.Contains(ResaleStarsAmountField) || doc[ResaleStarsAmountField].IsBsonNull)
        {
            return false;
        }

        var amountValue = doc[ResaleStarsAmountField];
        amount = amountValue.IsInt64 ? amountValue.AsInt64 : amountValue.AsInt32;
        if (amount <= 0)
        {
            return false;
        }

        if (doc.Contains(ResaleStarsNanosField) && !doc[ResaleStarsNanosField].IsBsonNull)
        {
            nanos = doc[ResaleStarsNanosField].AsInt32;
        }

        return true;
    }

    public static TVector<IStarsAmount>? BuildResellAmount(BsonDocument doc)
    {
        if (!TryGetResaleStarsAmount(doc, out var amount, out var nanos))
        {
            return null;
        }

        return
        [
            new TStarsAmount
            {
                Amount = amount,
                Nanos = nanos
            }
        ];
    }

    public static async Task RecalculateGiftResaleStatsAsync(
        IMongoCollection<BsonDocument> savedGiftsCollection,
        IMongoCollection<BsonDocument> giftsCollection,
        long giftId)
    {
        var listedFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("GiftId", giftId),
            Builders<BsonDocument>.Filter.Eq("Upgraded", true),
            Builders<BsonDocument>.Filter.Ne("Converted", true),
            Builders<BsonDocument>.Filter.Ne("Refunded", true),
            Builders<BsonDocument>.Filter.Gt(ResaleStarsAmountField, 0)
        );

        var listedCount = await savedGiftsCollection.CountDocumentsAsync(listedFilter);
        if (listedCount <= 0)
        {
            var clearUpdate = Builders<BsonDocument>.Update
                .Unset("AvailabilityResale")
                .Unset("ResellMinStars");
            await giftsCollection.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("GiftId", giftId),
                clearUpdate);
            return;
        }

        var minDoc = await savedGiftsCollection
            .Find(listedFilter)
            .Sort(Builders<BsonDocument>.Sort.Ascending(ResaleStarsAmountField))
            .Project(Builders<BsonDocument>.Projection.Include(ResaleStarsAmountField))
            .FirstOrDefaultAsync();

        var minStars = minDoc != null && minDoc.Contains(ResaleStarsAmountField) && !minDoc[ResaleStarsAmountField].IsBsonNull
            ? (minDoc[ResaleStarsAmountField].IsInt64 ? minDoc[ResaleStarsAmountField].AsInt64 : minDoc[ResaleStarsAmountField].AsInt32)
            : 0;

        var update = Builders<BsonDocument>.Update
            .Set("AvailabilityResale", (long)listedCount)
            .Set("ResellMinStars", minStars);

        await giftsCollection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("GiftId", giftId),
            update);
    }
}
