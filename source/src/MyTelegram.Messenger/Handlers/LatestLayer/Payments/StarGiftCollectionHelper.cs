using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal static class StarGiftCollectionHelper
{
    internal const string CollectionsCollectionName = "eventflow-stargiftcollectionreadmodel";
    internal const string SavedGiftsCollectionName = "eventflow-savedstargiftreadmodel";
    private const string GiftsCollectionName = "eventflow-stargiftreadmodel";
    private const string DocumentsCollectionName = "eventflow-documentreadmodel";

    public static IMongoCollection<BsonDocument> GetCollectionsCollection(IMongoDatabase mongoDatabase)
    {
        return mongoDatabase.GetCollection<BsonDocument>(CollectionsCollectionName);
    }

    public static IMongoCollection<BsonDocument> GetSavedGiftsCollection(IMongoDatabase mongoDatabase)
    {
        return mongoDatabase.GetCollection<BsonDocument>(SavedGiftsCollectionName);
    }

    public static List<long> GetGiftSavedIds(BsonDocument collectionDoc)
    {
        if (!collectionDoc.Contains("GiftSavedIds") ||
            collectionDoc["GiftSavedIds"].IsBsonNull ||
            !collectionDoc["GiftSavedIds"].IsBsonArray)
        {
            return [];
        }

        var result = new List<long>();
        foreach (var item in collectionDoc["GiftSavedIds"].AsBsonArray)
        {
            if (item.IsBsonNull)
            {
                continue;
            }

            result.Add(item.IsInt64 ? item.AsInt64 : item.AsInt32);
        }

        return result;
    }

    public static async Task<List<long>> ResolveSavedGiftIdsAsync(
        IMongoCollection<BsonDocument> savedGiftsCollection,
        IPeerHelper peerHelper,
        IEnumerable<IInputSavedStarGift> inputGifts,
        long ownerUserId,
        long requesterUserId)
    {
        var result = new List<long>();
        var unique = new HashSet<long>();

        foreach (var inputGift in inputGifts)
        {
            var savedGiftDoc = await ResolveSavedGiftAsync(savedGiftsCollection, peerHelper, inputGift, ownerUserId, requesterUserId);
            if (savedGiftDoc == null)
            {
                continue;
            }

            var savedId = GetLong(savedGiftDoc, "SavedId");
            if (savedId == 0 || !unique.Add(savedId))
            {
                continue;
            }

            result.Add(savedId);
        }

        return result;
    }

    public static async Task<BsonDocument?> ResolveSavedGiftAsync(
        IMongoCollection<BsonDocument> savedGiftsCollection,
        IPeerHelper peerHelper,
        IInputSavedStarGift inputGift,
        long ownerUserId,
        long requesterUserId)
    {
        return inputGift switch
        {
            TInputSavedStarGiftUser userGift => await ResolveUserGiftAsync(savedGiftsCollection, ownerUserId, userGift.MsgId),
            TInputSavedStarGiftChat chatGift => await ResolveChatGiftAsync(savedGiftsCollection, peerHelper, ownerUserId, requesterUserId, chatGift),
            TInputSavedStarGiftSlug slugGift => await ResolveSlugGiftAsync(savedGiftsCollection, ownerUserId, slugGift.Slug),
            _ => null
        };
    }

    public static long CalculateCollectionHash(int collectionId, string title, IReadOnlyCollection<long> savedGiftIds)
    {
        unchecked
        {
            long hash = 17;
            hash = hash * 31 + collectionId;
            hash = hash * 31 + GetStableStringHash(title);

            foreach (var savedGiftId in savedGiftIds)
            {
                hash = hash * 31 + savedGiftId;
            }

            return hash;
        }
    }

    public static long CalculateCollectionsHash(IEnumerable<IStarGiftCollection> collections)
    {
        unchecked
        {
            long hash = 17;
            var hasItems = false;
            foreach (var collection in collections)
            {
                hasItems = true;
                hash = hash * 31 + collection.CollectionId;
                hash = hash * 31 + collection.Hash;
            }

            return hasItems ? hash : 0;
        }
    }

    public static async Task<TStarGiftCollection> BuildCollectionAsync(
        IMongoDatabase mongoDatabase,
        BsonDocument collectionDoc)
    {
        var ownerUserId = GetLong(collectionDoc, "OwnerUserId");
        var collectionId = GetInt(collectionDoc, "CollectionId");
        var title = GetNullableString(collectionDoc, "Title") ?? string.Empty;
        var hash = collectionDoc.Contains("Hash") && !collectionDoc["Hash"].IsBsonNull
            ? GetLong(collectionDoc, "Hash")
            : CalculateCollectionHash(collectionId, title, GetGiftSavedIds(collectionDoc));

        var giftSavedIds = GetGiftSavedIds(collectionDoc);
        var icon = await TryGetCollectionIconAsync(mongoDatabase, ownerUserId, collectionId, giftSavedIds);

        return new TStarGiftCollection
        {
            CollectionId = collectionId,
            Title = title,
            GiftsCount = giftSavedIds.Count,
            Icon = icon,
            Hash = hash
        };
    }

    public static BsonArray ToBsonArray(IEnumerable<long> values)
    {
        var array = new BsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    public static int GetInt(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull)
        {
            return 0;
        }

        return doc[field].IsInt32 ? doc[field].AsInt32 : (int)doc[field].AsInt64;
    }

    public static long GetLong(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull)
        {
            return 0;
        }

        return doc[field].IsInt64 ? doc[field].AsInt64 : doc[field].AsInt32;
    }

    public static string? GetNullableString(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull)
        {
            return null;
        }

        return doc[field].AsString;
    }

    private static async Task<BsonDocument?> ResolveUserGiftAsync(
        IMongoCollection<BsonDocument> savedGiftsCollection,
        long ownerUserId,
        int msgId)
    {
        var byMsgId = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("OwnerUserId", ownerUserId),
            Builders<BsonDocument>.Filter.Eq("MsgId", msgId)
        );

        var doc = await savedGiftsCollection.Find(byMsgId).FirstOrDefaultAsync();
        if (doc != null)
        {
            return doc;
        }

        var bySavedId = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("OwnerUserId", ownerUserId),
            Builders<BsonDocument>.Filter.Eq("SavedId", (long)msgId)
        );

        return await savedGiftsCollection.Find(bySavedId).FirstOrDefaultAsync();
    }

    private static async Task<BsonDocument?> ResolveChatGiftAsync(
        IMongoCollection<BsonDocument> savedGiftsCollection,
        IPeerHelper peerHelper,
        long ownerUserId,
        long requesterUserId,
        TInputSavedStarGiftChat chatGift)
    {
        if (chatGift.SavedId == 0)
        {
            RpcErrors.RpcErrors400.SavedIdEmpty.ThrowRpcError();
        }

        var peer = peerHelper.GetPeer(chatGift.Peer, requesterUserId);
        if (peer.PeerId != ownerUserId)
        {
            return null;
        }

        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("OwnerUserId", ownerUserId),
            Builders<BsonDocument>.Filter.Eq("SavedId", chatGift.SavedId)
        );

        return await savedGiftsCollection.Find(filter).FirstOrDefaultAsync();
    }

    private static async Task<BsonDocument?> ResolveSlugGiftAsync(
        IMongoCollection<BsonDocument> savedGiftsCollection,
        long ownerUserId,
        string slug)
    {
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("OwnerUserId", ownerUserId),
            Builders<BsonDocument>.Filter.Eq("Slug", slug)
        );

        return await savedGiftsCollection.Find(filter).FirstOrDefaultAsync();
    }

    private static async Task<IDocument?> TryGetCollectionIconAsync(
        IMongoDatabase mongoDatabase,
        long ownerUserId,
        int collectionId,
        IReadOnlyCollection<long> orderedSavedIds)
    {
        var savedGiftsCollection = GetSavedGiftsCollection(mongoDatabase);

        BsonDocument? savedGiftDoc = null;
        foreach (var savedId in orderedSavedIds)
        {
            var filter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("OwnerUserId", ownerUserId),
                Builders<BsonDocument>.Filter.Eq("SavedId", savedId)
            );
            savedGiftDoc = await savedGiftsCollection.Find(filter).FirstOrDefaultAsync();
            if (savedGiftDoc != null)
            {
                break;
            }
        }

        if (savedGiftDoc == null)
        {
            var fallbackFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("OwnerUserId", ownerUserId),
                Builders<BsonDocument>.Filter.AnyEq("CollectionId", collectionId)
            );
            savedGiftDoc = await savedGiftsCollection.Find(fallbackFilter).FirstOrDefaultAsync();
        }

        if (savedGiftDoc == null)
        {
            return null;
        }

        var giftId = GetLong(savedGiftDoc, "GiftId");
        if (giftId == 0)
        {
            return null;
        }

        var giftsCollection = mongoDatabase.GetCollection<BsonDocument>(GiftsCollectionName);
        var giftDoc = await giftsCollection.Find(Builders<BsonDocument>.Filter.Eq("GiftId", giftId)).FirstOrDefaultAsync();
        if (giftDoc == null)
        {
            return null;
        }

        var stickerId = GetLong(giftDoc, "StickerId");
        if (stickerId == 0)
        {
            return null;
        }

        var documentsCollection = mongoDatabase.GetCollection<BsonDocument>(DocumentsCollectionName);
        var documentDoc = await documentsCollection.Find(Builders<BsonDocument>.Filter.Eq("DocumentId", stickerId)).FirstOrDefaultAsync();
        if (documentDoc == null)
        {
            return new TDocumentEmpty { Id = stickerId };
        }

        return new TDocument
        {
            Id = GetLong(documentDoc, "DocumentId"),
            AccessHash = GetLong(documentDoc, "AccessHash"),
            Date = GetInt(documentDoc, "Date"),
            MimeType = GetNullableString(documentDoc, "MimeType") ?? string.Empty,
            Size = GetLong(documentDoc, "Size"),
            DcId = GetInt(documentDoc, "DcId"),
            FileReference = documentDoc.Contains("FileReference") && !documentDoc["FileReference"].IsBsonNull
                ? documentDoc["FileReference"].AsByteArray
                : [],
            Attributes = []
        };
    }

    private static int GetStableStringHash(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        unchecked
        {
            var hash = 17;
            foreach (var c in value)
            {
                hash = hash * 31 + c;
            }

            return hash;
        }
    }
}
