using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Converters.ConverterServices;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Transfer a <a href="https://corefork.telegram.org/api/gifts#collectible-gifts">collectible gift</a> to another user or channel: can only be used if transfer is free (i.e. <a href="https://corefork.telegram.org/constructor/messageActionStarGiftUnique">messageActionStarGiftUnique</a>.<code>transfer_stars</code> is not set); see <a href="https://corefork.telegram.org/api/gifts#transferring-collectible-gifts">here »</a> for more info on the full flow (including the different flow to use in case the transfer isn't free).
/// Possible errors
/// Code Type Description
/// 400 BUSINESS_CONNECTION_INVALID The <code>connection_id</code> passed to the wrapping <a href="https://corefork.telegram.org/api/business">invokeWithBusinessConnection</a> call is invalid.
/// 400 MESSAGE_ID_INVALID The provided message id is invalid.
/// 400 PAYMENT_REQUIRED Payment is required for this action, see <a href="https://corefork.telegram.org/api/gifts">here »</a> for more info.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 SAVED_ID_EMPTY The passed inputSavedStarGiftChat.saved_id is empty.
/// 400 STARGIFT_NOT_FOUND The specified gift was not found.
/// 400 STARGIFT_OWNER_INVALID You cannot transfer or sell a gift owned by another user.
/// 400 STARGIFT_PEER_INVALID The specified inputSavedStarGiftChat.peer is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.transferStarGift"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class TransferStarGiftHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IIdGenerator idGenerator,
    IUserConverterService userConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestTransferStarGift, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestTransferStarGift obj)
    {
        var userId = input.UserId;
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var savedGiftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-savedstargiftreadmodel");

        FilterDefinition<BsonDocument> filter;
        BsonDocument? savedGiftDoc = null;

        switch (obj.Stargift)
        {
            case TInputSavedStarGiftUser userGift:
                // First try to find by MsgId
                filter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("OwnerUserId", userId),
                    Builders<BsonDocument>.Filter.Eq("MsgId", userGift.MsgId)
                );
                savedGiftDoc = await savedGiftsCollection.Find(filter).FirstOrDefaultAsync();
                
                // Fallback: try to find by SavedId (client may send SavedId as MsgId)
                if (savedGiftDoc == null)
                {
                    filter = Builders<BsonDocument>.Filter.And(
                        Builders<BsonDocument>.Filter.Eq("OwnerUserId", userId),
                        Builders<BsonDocument>.Filter.Eq("SavedId", (long)userGift.MsgId)
                    );
                    savedGiftDoc = await savedGiftsCollection.Find(filter).FirstOrDefaultAsync();
                }
                break;

            case TInputSavedStarGiftChat chatGift:
                var chatPeer = peerHelper.GetPeer(chatGift.Peer, userId);
                if (chatGift.SavedId == 0)
                {
                    RpcErrors.RpcErrors400.SavedIdEmpty.ThrowRpcError();
                }
                filter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("OwnerUserId", chatPeer.PeerId),
                    Builders<BsonDocument>.Filter.Eq("SavedId", chatGift.SavedId)
                );
                savedGiftDoc = await savedGiftsCollection.Find(filter).FirstOrDefaultAsync();
                break;

            case TInputSavedStarGiftSlug slugGift:
                filter = Builders<BsonDocument>.Filter.Eq("Slug", slugGift.Slug);
                savedGiftDoc = await savedGiftsCollection.Find(filter).FirstOrDefaultAsync();
                break;

            default:
                RpcErrors.RpcErrors400.StargiftPeerInvalid.ThrowRpcError();
                return new TUpdates();
        }

        if (savedGiftDoc == null)
        {
            RpcErrors.RpcErrors400.StargiftNotFound.ThrowRpcError();
        }

        // Check ownership
        var ownerUserId = GetLong(savedGiftDoc!, "OwnerUserId");
        if (ownerUserId != userId)
        {
            RpcErrors.RpcErrors400.StargiftOwnerInvalid.ThrowRpcError();
        }

        // Check if gift is upgraded (only upgraded/collectible gifts can be transferred)
        if (!savedGiftDoc!.GetValue("Upgraded", false).AsBoolean)
        {
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
        }

        // Check if transfer requires payment
        var transferStars = GetNullableLong(savedGiftDoc, "TransferStars");
        if (transferStars.HasValue && transferStars.Value > 0)
        {
            RpcErrors.RpcErrors400.PaymentRequired.ThrowRpcError();
        }

        // Get recipient peer
        var recipientPeer = peerHelper.GetPeer(obj.ToId, userId);
        if (recipientPeer.PeerType != PeerType.User && recipientPeer.PeerType != PeerType.Channel)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var recipientId = recipientPeer.PeerId;

        // Generate new saved ID for recipient
        var newSavedId = await idGenerator.NextIdAsync(IdType.SavedStarGiftId, recipientId);

        // Update gift ownership
        var updateGift = Builders<BsonDocument>.Update
            .Set("OwnerUserId", recipientId)
            .Set("SavedId", (long)newSavedId)
            .Set("TransferredFrom", userId)
            .Set("TransferDate", now)
            .Set("CanTransferAt", now)
            .Set("CanResellAt", now)
            .Set("Saved", false)
            .Set("PinnedToTop", false)
            .Unset("PinnedOrder");
        await savedGiftsCollection.UpdateOneAsync(filter, updateGift);

        // Get user info
        var userIds = new List<long> { userId };
        if (recipientPeer.PeerType == PeerType.User)
        {
            userIds.Add(recipientId);
        }
        var userList = await userConverterService.GetUserListAsync(input, userIds, true, true, input.Layer);
        var users = new TVector<IUser>();
        foreach (var user in userList)
        {
            users.Add(user);
        }

        return new TUpdates
        {
            Updates = [],
            Users = users,
            Chats = [],
            Date = now,
            Seq = 0
        };
    }

    private static long GetLong(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull) return 0;
        var value = doc[field];
        return value.IsInt64 ? value.AsInt64 : value.AsInt32;
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
}
