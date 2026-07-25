using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarsTransactions;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Convert a <a href="https://corefork.telegram.org/api/gifts">received gift »</a> into Telegram Stars: this will permanently destroy the gift, converting it into <a href="https://corefork.telegram.org/constructor/starGift">starGift</a>.<code>convert_stars</code> <a href="https://corefork.telegram.org/api/stars">Telegram Stars</a>, added to the user's balance.Note that <a href="https://corefork.telegram.org/constructor/starGift">starGift</a>.<code>convert_stars</code> will be less than the buying price (<a href="https://corefork.telegram.org/constructor/starGift">starGift</a>.<code>stars</code>) of the gift if it was originally bought using Telegram Stars bought a long time ago.
/// Possible errors
/// Code Type Description
/// 400 BUSINESS_CONNECTION_INVALID The <code>connection_id</code> passed to the wrapping <a href="https://corefork.telegram.org/api/business">invokeWithBusinessConnection</a> call is invalid.
/// 400 MESSAGE_ID_INVALID The provided message id is invalid.
/// 400 SAVED_ID_EMPTY The passed inputSavedStarGiftChat.saved_id is empty.
/// 400 STARGIFT_PEER_INVALID The specified inputSavedStarGiftChat.peer is invalid.
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.convertStarGift"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ConvertStarGiftHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestConvertStarGift, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestConvertStarGift obj)
    {
        var userId = input.UserId;
        var savedGiftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-savedstargiftreadmodel");
        var giftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stargiftreadmodel");
        var balanceCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-userstarsbalancereadmodel");

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
                var peer = peerHelper.GetPeer(chatGift.Peer, userId);
                if (chatGift.SavedId == 0)
                {
                    RpcErrors.RpcErrors400.SavedIdEmpty.ThrowRpcError();
                }
                filter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("OwnerUserId", peer.PeerId),
                    Builders<BsonDocument>.Filter.Eq("SavedId", chatGift.SavedId)
                );
                savedGiftDoc = await savedGiftsCollection.Find(filter).FirstOrDefaultAsync();
                break;

            case TInputSavedStarGiftSlug slugGift:
                filter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("OwnerUserId", userId),
                    Builders<BsonDocument>.Filter.Eq("Slug", slugGift.Slug)
                );
                savedGiftDoc = await savedGiftsCollection.Find(filter).FirstOrDefaultAsync();
                break;

            default:
                RpcErrors.RpcErrors400.StargiftPeerInvalid.ThrowRpcError();
                return new TBoolFalse();
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

        // Check if already converted
        if (savedGiftDoc!.GetValue("Converted", false).AsBoolean)
        {
            RpcErrors.RpcErrors400.StargiftAlreadyConverted.ThrowRpcError();
        }

        // Check if upgraded (upgraded gifts cannot be converted)
        if (savedGiftDoc.GetValue("Upgraded", false).AsBoolean)
        {
            RpcErrors.RpcErrors400.StargiftAlreadyUpgraded.ThrowRpcError();
        }

        // Get convert stars amount from saved gift or from gift definition
        var convertStars = GetNullableLong(savedGiftDoc, "ConvertStars");
        if (!convertStars.HasValue)
        {
            var giftId = GetLong(savedGiftDoc, "GiftId");
            var giftDoc = await giftsCollection.Find(
                Builders<BsonDocument>.Filter.Eq("GiftId", giftId)
            ).FirstOrDefaultAsync();

            if (giftDoc != null)
            {
                convertStars = GetLong(giftDoc, "ConvertStars");
            }
        }

        if (!convertStars.HasValue || convertStars.Value <= 0)
        {
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
        }

        // Add stars to user's balance
        var balanceFilter = Builders<BsonDocument>.Filter.Eq("UserId", userId);
        var balanceDoc = await balanceCollection.Find(balanceFilter).FirstOrDefaultAsync();

        long currentBalance = 0;
        if (balanceDoc != null && balanceDoc.Contains("Balance"))
        {
            currentBalance = balanceDoc["Balance"].IsInt64 ? balanceDoc["Balance"].AsInt64 : balanceDoc["Balance"].AsInt32;
        }

        var newBalance = currentBalance + convertStars.Value;
        if (balanceDoc != null)
        {
            var updateBalance = Builders<BsonDocument>.Update
                .Set("Balance", newBalance)
                .Set("LastUpdated", DateTime.UtcNow);
            await balanceCollection.UpdateOneAsync(balanceFilter, updateBalance);
        }
        else
        {
            var newBalanceDoc = new BsonDocument
            {
                { "UserId", userId },
                { "Balance", newBalance },
                { "LastUpdated", DateTime.UtcNow }
            };
            await balanceCollection.InsertOneAsync(newBalanceDoc);
        }

        // Mark gift as converted
        var updateGift = Builders<BsonDocument>.Update
            .Set("Converted", true)
            .Set("ConvertedDate", (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await savedGiftsCollection.UpdateOneAsync(filter, updateGift);

        var convertedGiftId = GetLong(savedGiftDoc, "GiftId");
        var giftDocForTitle = await giftsCollection.Find(
            Builders<BsonDocument>.Filter.Eq("GiftId", convertedGiftId)
        ).FirstOrDefaultAsync();
        var giftTitle = giftDocForTitle != null ? GetNullableString(giftDocForTitle, "Title") : null;
        var convertTransaction = StarsTransactionStore.CreateTransactionDocument(
            userId,
            convertStars.Value,
            (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            (int)PeerType.User,
            userId,
            giftId: convertedGiftId,
            title: giftTitle ?? "Star Gift",
            description: "Gift conversion to Stars"
        );
        await StarsTransactionStore.GetCollection(mongoDatabase).InsertOneAsync(convertTransaction);

        return new TBoolTrue();
    }

    private static long GetLong(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull) return 0;
        var value = doc[field];
        return value.IsInt64 ? value.AsInt64 : value.AsInt32;
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
