using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Pins a received gift on top of the profile of the user or owned channels by using <a href="https://corefork.telegram.org/method/payments.toggleStarGiftsPinnedToTop">payments.toggleStarGiftsPinnedToTop</a>.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.toggleStarGiftsPinnedToTop"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ToggleStarGiftsPinnedToTopHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestToggleStarGiftsPinnedToTop, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestToggleStarGiftsPinnedToTop obj)
    {
        var userId = input.UserId;
        var savedGiftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-savedstargiftreadmodel");

        // Get the peer whose gifts we're pinning
        var peer = peerHelper.GetPeer(obj.Peer, userId);
        var ownerUserId = peer.PeerId;

        // First, unpin all currently pinned gifts for this owner
        var unpinFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("OwnerUserId", ownerUserId),
            Builders<BsonDocument>.Filter.Eq("PinnedToTop", true)
        );
        var unpinUpdate = Builders<BsonDocument>.Update
            .Set("PinnedToTop", false)
            .Unset("PinnedOrder");
        await savedGiftsCollection.UpdateManyAsync(unpinFilter, unpinUpdate);

        // Now pin the specified gifts
        var pinnedSavedIds = new HashSet<long>();
        var pinnedOrder = 0;
        foreach (var inputGift in obj.Stargift)
        {
            var savedGiftDoc = await ResolveSavedGiftAsync(savedGiftsCollection, peerHelper, inputGift, ownerUserId, userId);
            if (savedGiftDoc == null)
            {
                continue;
            }

            var savedId = GetLong(savedGiftDoc, "SavedId");
            if (savedId == 0 || !pinnedSavedIds.Add(savedId))
            {
                continue;
            }

            var filter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("OwnerUserId", ownerUserId),
                Builders<BsonDocument>.Filter.Eq("SavedId", savedId)
            );
            var pinUpdate = Builders<BsonDocument>.Update
                .Set("PinnedToTop", true)
                .Set("Saved", true) // Pinned gifts are automatically saved
                .Set("PinnedOrder", pinnedOrder++);
            await savedGiftsCollection.UpdateOneAsync(filter, pinUpdate);
        }

        return new TBoolTrue();
    }

    private static async Task<BsonDocument?> ResolveSavedGiftAsync(
        IMongoCollection<BsonDocument> savedGiftsCollection,
        IPeerHelper peerHelper,
        IInputSavedStarGift inputGift,
        long ownerUserId,
        long requesterUserId)
    {
        return inputGift switch
        {
            TInputSavedStarGiftUser userGift => await ResolveUserGiftAsync(savedGiftsCollection, ownerUserId, userGift.MsgId),
            TInputSavedStarGiftChat chatGift => await ResolveChatGiftAsync(savedGiftsCollection, peerHelper, chatGift, ownerUserId, requesterUserId),
            TInputSavedStarGiftSlug slugGift => await ResolveSlugGiftAsync(savedGiftsCollection, ownerUserId, slugGift.Slug),
            _ => null
        };
    }

    private static async Task<BsonDocument?> ResolveUserGiftAsync(
        IMongoCollection<BsonDocument> savedGiftsCollection,
        long ownerUserId,
        int msgId)
    {
        var msgFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("OwnerUserId", ownerUserId),
            Builders<BsonDocument>.Filter.Eq("MsgId", msgId)
        );
        var savedGiftDoc = await savedGiftsCollection.Find(msgFilter).FirstOrDefaultAsync();
        if (savedGiftDoc != null)
        {
            return savedGiftDoc;
        }

        var savedIdFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("OwnerUserId", ownerUserId),
            Builders<BsonDocument>.Filter.Eq("SavedId", (long)msgId)
        );
        return await savedGiftsCollection.Find(savedIdFilter).FirstOrDefaultAsync();
    }

    private static async Task<BsonDocument?> ResolveChatGiftAsync(
        IMongoCollection<BsonDocument> savedGiftsCollection,
        IPeerHelper peerHelper,
        TInputSavedStarGiftChat chatGift,
        long ownerUserId,
        long requesterUserId)
    {
        if (chatGift.SavedId == 0)
        {
            return null;
        }

        var chatPeer = peerHelper.GetPeer(chatGift.Peer, requesterUserId);
        if (chatPeer.PeerId != ownerUserId)
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

    private static long GetLong(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull) return 0;
        var value = doc[field];
        return value.IsInt64 ? value.AsInt64 : value.AsInt32;
    }
}
