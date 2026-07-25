using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// A <a href="https://corefork.telegram.org/api/gifts#collectible-gifts">collectible gift we own В»</a> can be put up for sale on the <a href="https://telegram.org/blog/gift-marketplace-and-more">gift marketplace В»</a> with this method, see <a href="https://corefork.telegram.org/api/gifts#reselling-collectible-gifts">here В»</a> for more info.
/// Possible errors
/// Code Type Description
/// 400 SAVED_ID_EMPTY The passed inputSavedStarGiftChat.saved_id is empty.
/// 400 STARGIFT_NOT_FOUND The specified gift was not found.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.updateStarGiftPrice"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User вњ”] [Bot вњ–] [Anonymous вњ–]
/// </remarks>
internal sealed class UpdateStarGiftPriceHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestUpdateStarGiftPrice, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestUpdateStarGiftPrice obj)
    {
        var userId = input.UserId;
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var savedGiftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-savedstargiftreadmodel");
        var giftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stargiftreadmodel");

        BsonDocument? savedGiftDoc = null;
        FilterDefinition<BsonDocument> filter;

        switch (obj.Stargift)
        {
            case TInputSavedStarGiftUser userGift:
                filter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("OwnerUserId", userId),
                    Builders<BsonDocument>.Filter.Eq("MsgId", userGift.MsgId)
                );
                savedGiftDoc = await savedGiftsCollection.Find(filter).FirstOrDefaultAsync();

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
                throw new RpcException(RpcErrors.RpcErrors400.StargiftInvalid);
        }

        if (savedGiftDoc == null)
        {
            RpcErrors.RpcErrors400.StargiftNotFound.ThrowRpcError();
        }

        var ownerUserId = GetLong(savedGiftDoc!, "OwnerUserId");
        if (ownerUserId != userId)
        {
            RpcErrors.RpcErrors400.StargiftOwnerInvalid.ThrowRpcError();
        }

        if (!savedGiftDoc.GetValue("Upgraded", false).AsBoolean ||
            savedGiftDoc.GetValue("Converted", false).AsBoolean ||
            savedGiftDoc.GetValue("Refunded", false).AsBoolean)
        {
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
        }

        UpdateDefinition<BsonDocument> update;
        switch (obj.ResellAmount)
        {
            case TStarsTonAmount:
                RpcErrors.RpcErrors400.StargiftResellCurrencyNotAllowed.ThrowRpcError();
                return null!;

            case TStarsAmount starsAmount:
                if (starsAmount.Nanos != 0 || starsAmount.Amount < 0)
                {
                    RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
                }

                if (starsAmount.Amount == 0)
                {
                    update = Builders<BsonDocument>.Update
                        .Unset(StarGiftResaleHelper.ResaleStarsAmountField)
                        .Unset(StarGiftResaleHelper.ResaleStarsNanosField)
                        .Set(StarGiftResaleHelper.ResaleUpdatedAtField, now);
                }
                else
                {
                    update = Builders<BsonDocument>.Update
                        .Set(StarGiftResaleHelper.ResaleStarsAmountField, starsAmount.Amount)
                        .Set(StarGiftResaleHelper.ResaleStarsNanosField, starsAmount.Nanos)
                        .Set(StarGiftResaleHelper.ResaleUpdatedAtField, now);
                }
                break;

            default:
                RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
                return null!;
        }

        await savedGiftsCollection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", savedGiftDoc["_id"]),
            update);

        var giftId = GetLong(savedGiftDoc, "GiftId");
        await StarGiftResaleHelper.RecalculateGiftResaleStatsAsync(savedGiftsCollection, giftsCollection, giftId);

        return new TUpdates
        {
            Updates = [],
            Users = [],
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
}
