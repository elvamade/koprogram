using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Services.StarsTransactions;

internal static class StarsTransactionQueryHelper
{
    internal static async Task<long> GetBalanceAsync(IMongoDatabase database, long userId)
    {
        var balanceCollection = database.GetCollection<BsonDocument>("eventflow-userstarsbalancereadmodel");
        var balanceFilter = Builders<BsonDocument>.Filter.Eq("UserId", userId);
        var balanceDoc = await balanceCollection.Find(balanceFilter).FirstOrDefaultAsync();

        if (balanceDoc == null || !balanceDoc.Contains("Balance"))
        {
            return 0;
        }

        return balanceDoc["Balance"].IsInt64 ? balanceDoc["Balance"].AsInt64 : balanceDoc["Balance"].AsInt32;
    }

    internal static async Task<(TVector<IStarsTransaction> History, TVector<IUser> Users)> BuildHistoryAsync(
        IMongoDatabase database,
        IUserConverterService userConverterService,
        IRequestInput input,
        IReadOnlyCollection<BsonDocument> docs,
        bool ton)
    {
        if (docs.Count == 0)
        {
            return ([], []);
        }

        var giftsCollection = database.GetCollection<BsonDocument>("eventflow-stargiftreadmodel");
        var documentsCollection = database.GetCollection<BsonDocument>("eventflow-documentreadmodel");

        var giftIds = docs
            .Where(d => d.Contains("GiftId") && !d["GiftId"].IsBsonNull)
            .Select(d => StarsTransactionStore.GetLong(d, "GiftId"))
            .Distinct()
            .ToList();

        var giftMap = new Dictionary<long, BsonDocument>();
        if (giftIds.Count > 0)
        {
            var giftDocs = await giftsCollection.Find(Builders<BsonDocument>.Filter.In("GiftId", giftIds)).ToListAsync();
            foreach (var giftDoc in giftDocs)
            {
                giftMap[StarsTransactionStore.GetLong(giftDoc, "GiftId")] = giftDoc;
            }
        }

        var stickerIds = giftMap.Values
            .Select(g => StarsTransactionStore.GetLong(g, "StickerId"))
            .Distinct()
            .ToList();

        var stickerMap = new Dictionary<long, BsonDocument>();
        if (stickerIds.Count > 0)
        {
            var stickerDocs = await documentsCollection.Find(Builders<BsonDocument>.Filter.In("DocumentId", stickerIds)).ToListAsync();
            foreach (var stickerDoc in stickerDocs)
            {
                stickerMap[StarsTransactionStore.GetLong(stickerDoc, "DocumentId")] = stickerDoc;
            }
        }

        var history = new TVector<IStarsTransaction>();
        var userIds = new HashSet<long>();

        foreach (var doc in docs)
        {
            IStarGift? stargift = null;
            if (doc.Contains("GiftId") && !doc["GiftId"].IsBsonNull)
            {
                var giftId = StarsTransactionStore.GetLong(doc, "GiftId");
                if (giftMap.TryGetValue(giftId, out var giftDoc))
                {
                    stargift = BuildStarGift(giftDoc, stickerMap);
                }
            }

            var tx = StarsTransactionStore.ToStarsTransaction(doc, ton, stargift);
            history.Add(tx);

            if (doc.Contains("PeerType") && doc.Contains("PeerId"))
            {
                var peerType = StarsTransactionStore.GetInt(doc, "PeerType");
                var peerId = StarsTransactionStore.GetLong(doc, "PeerId");
                if (peerType == (int)PeerType.User && peerId > 0)
                {
                    userIds.Add(peerId);
                }
            }
        }

        var users = new TVector<IUser>();
        if (userIds.Count > 0)
        {
            var userList = await userConverterService.GetUserListAsync(input, userIds.ToList(), true, true, input.Layer);
            foreach (var user in userList)
            {
                users.Add(user);
            }
        }

        return (history, users);
    }

    private static IStarGift BuildStarGift(BsonDocument giftDoc, Dictionary<long, BsonDocument> stickerMap)
    {
        var stickerId = StarsTransactionStore.GetLong(giftDoc, "StickerId");
        IDocument sticker;
        if (stickerMap.TryGetValue(stickerId, out var stickerDoc))
        {
            sticker = ConvertDocument(stickerDoc);
        }
        else
        {
            sticker = new TDocumentEmpty { Id = stickerId };
        }

        return new TStarGift
        {
            Id = StarsTransactionStore.GetLong(giftDoc, "GiftId"),
            Limited = giftDoc.GetValue("Limited", false).AsBoolean,
            SoldOut = giftDoc.GetValue("SoldOut", false).AsBoolean,
            Birthday = giftDoc.GetValue("Birthday", false).AsBoolean,
            RequirePremium = giftDoc.GetValue("RequirePremium", false).AsBoolean,
            LimitedPerUser = giftDoc.GetValue("LimitedPerUser", false).AsBoolean,
            Sticker = sticker,
            Stars = StarsTransactionStore.GetLong(giftDoc, "Stars"),
            ConvertStars = StarsTransactionStore.GetLong(giftDoc, "ConvertStars"),
            AvailabilityRemains = GetNullableInt(giftDoc, "AvailabilityRemains"),
            AvailabilityTotal = GetNullableInt(giftDoc, "AvailabilityTotal"),
            AvailabilityResale = GetNullableLong(giftDoc, "AvailabilityResale"),
            FirstSaleDate = GetNullableInt(giftDoc, "FirstSaleDate"),
            LastSaleDate = GetNullableInt(giftDoc, "LastSaleDate"),
            UpgradeStars = GetNullableLong(giftDoc, "UpgradeStars"),
            ResellMinStars = GetNullableLong(giftDoc, "ResellMinStars"),
            Title = GetNullableString(giftDoc, "Title")
        };
    }

    private static IDocument ConvertDocument(BsonDocument doc)
    {
        return new TDocument
        {
            Id = StarsTransactionStore.GetLong(doc, "DocumentId"),
            AccessHash = StarsTransactionStore.GetLong(doc, "AccessHash"),
            Date = StarsTransactionStore.GetInt(doc, "Date"),
            MimeType = doc["MimeType"].AsString,
            Size = StarsTransactionStore.GetLong(doc, "Size"),
            DcId = doc["DcId"].AsInt32,
            FileReference = doc.Contains("FileReference") && !doc["FileReference"].IsBsonNull
                ? doc["FileReference"].AsByteArray
                : Array.Empty<byte>(),
            Attributes = new TVector<IDocumentAttribute>()
        };
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

    private static string? GetNullableString(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull) return null;
        return doc[field].AsString;
    }
}
