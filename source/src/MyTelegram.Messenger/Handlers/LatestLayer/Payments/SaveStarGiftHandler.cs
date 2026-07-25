namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

using MongoDB.Bson;
using MongoDB.Driver;

/// <summary>
/// Display or remove a <a href="https://corefork.telegram.org/api/gifts">received gift »</a> from our profile.
/// Possible errors
/// Code Type Description
/// 400 MESSAGE_ID_INVALID The provided message id is invalid.
/// 400 SAVED_ID_EMPTY The passed inputSavedStarGiftChat.saved_id is empty.
/// 400 STARGIFT_OWNER_INVALID You cannot transfer or sell a gift owned by another user.
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.saveStarGift"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SaveStarGiftHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestSaveStarGift, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestSaveStarGift obj)
    {
        var userId = input.UserId;
        var savedGiftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-savedstargiftreadmodel");

        FilterDefinition<BsonDocument> filter;

        switch (obj.Stargift)
        {
            case TInputSavedStarGiftUser userGift:
                // Find by message ID for user's own gifts
                filter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("OwnerUserId", userId),
                    Builders<BsonDocument>.Filter.Eq("MsgId", userGift.MsgId)
                );
                break;

            case TInputSavedStarGiftChat chatGift:
                // Find by saved_id for channel/chat gifts
                var peer = peerHelper.GetPeer(chatGift.Peer, userId);
                if (chatGift.SavedId == 0)
                {
                    RpcErrors.RpcErrors400.SavedIdEmpty.ThrowRpcError();
                }
                filter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("OwnerUserId", peer.PeerId),
                    Builders<BsonDocument>.Filter.Eq("SavedId", chatGift.SavedId)
                );
                break;

            case TInputSavedStarGiftSlug slugGift:
                // Find by slug for collectible gifts
                filter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("OwnerUserId", userId),
                    Builders<BsonDocument>.Filter.Eq("Slug", slugGift.Slug)
                );
                break;

            default:
                throw new RpcException(RpcErrors.RpcErrors400.StargiftInvalid);
        }

        var savedGiftDoc = await savedGiftsCollection.Find(filter).FirstOrDefaultAsync();

        if (savedGiftDoc == null)
        {
            // Try to find by SavedId directly for user gifts
            if (obj.Stargift is TInputSavedStarGiftUser userGift2)
            {
                filter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("OwnerUserId", userId),
                    Builders<BsonDocument>.Filter.Eq("SavedId", (long)userGift2.MsgId)
                );
                savedGiftDoc = await savedGiftsCollection.Find(filter).FirstOrDefaultAsync();
            }
        }

        if (savedGiftDoc == null)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        // Check ownership
        var ownerUserId = GetLong(savedGiftDoc!, "OwnerUserId");
        if (ownerUserId != userId)
        {
            RpcErrors.RpcErrors400.StargiftOwnerInvalid.ThrowRpcError();
        }

        // Toggle saved status
        var newSavedStatus = !obj.Unsave;
        var update = Builders<BsonDocument>.Update.Set("Saved", newSavedStatus);
        if (!newSavedStatus)
        {
            update = update
                .Set("PinnedToTop", false)
                .Unset("PinnedOrder");
        }
        await savedGiftsCollection.UpdateOneAsync(filter, update);

        return new TBoolTrue();
    }

    private static long GetLong(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull) return 0;
        var value = doc[field];
        return value.IsInt64 ? value.AsInt64 : value.AsInt32;
    }
}
