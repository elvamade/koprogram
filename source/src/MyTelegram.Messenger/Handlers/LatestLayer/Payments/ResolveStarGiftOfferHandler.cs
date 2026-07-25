
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarsTransactions;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

/// <summary>
/// <para><c>See <a href="" /> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ] [Bot ] [Anonymous ]
/// </remarks>
internal sealed class ResolveStarGiftOfferHandler(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor,
    IIdGenerator idGenerator,
    IUserConverterService userConverterService,
    IObjectMessageSender objectMessageSender) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestResolveStarGiftOffer, MyTelegram.Schema.IUpdates>, IObjectHandler
{
    private const string OffersCollectionName = "eventflow-stargiftofferreadmodel";
    private const string SavedGiftsCollectionName = "eventflow-savedstargiftreadmodel";
    private const string GiftsCollectionName = "eventflow-stargiftreadmodel";
    private const string BalanceCollectionName = "eventflow-userstarsbalancereadmodel";

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestResolveStarGiftOffer obj)
    {
        var buyerId = input.UserId;
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (obj.OfferMsgId <= 0)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        var offersCollection = mongoDatabase.GetCollection<BsonDocument>(OffersCollectionName);
        var messageReadModel = await queryProcessor.ProcessAsync(new GetMessageByIdQuery(MessageId.Create(buyerId, obj.OfferMsgId).Value));
        var offerAction = messageReadModel?.MessageAction as TMessageActionStarGiftPurchaseOffer;
        BsonDocument? offerDoc = null;

        long sellerId;
        int sellerMessageId;
        var messageDate = now;
        if (messageReadModel != null && offerAction != null)
        {
            sellerId = messageReadModel.SenderUserId;
            sellerMessageId = messageReadModel.SenderMessageId;
            messageDate = messageReadModel.Date;

            var offerFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("SenderUserId", sellerId),
                Builders<BsonDocument>.Filter.Eq("RecipientUserId", buyerId),
                Builders<BsonDocument>.Filter.Eq("SenderMessageId", sellerMessageId)
            );
            offerDoc = await offersCollection.Find(offerFilter)
                .Sort(Builders<BsonDocument>.Sort.Descending("CreatedAt"))
                .FirstOrDefaultAsync();

            if (offerDoc == null)
            {
                offerDoc = await CreateOfferFromMessageAsync(offersCollection, offerAction, sellerId, buyerId, sellerMessageId, now);
            }
        }
        else
        {
            var fallbackOfferFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("RecipientUserId", buyerId),
                Builders<BsonDocument>.Filter.Or(
                    Builders<BsonDocument>.Filter.Eq("RecipientMessageId", obj.OfferMsgId),
                    Builders<BsonDocument>.Filter.Eq("SenderMessageId", obj.OfferMsgId)
                ));
            offerDoc = await offersCollection.Find(fallbackOfferFilter)
                .Sort(Builders<BsonDocument>.Sort.Descending("CreatedAt"))
                .FirstOrDefaultAsync();
            if (offerDoc == null)
            {
                RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
            }

            sellerId = GetLong(offerDoc!, "SenderUserId");
            sellerMessageId = GetNullableInt(offerDoc, "SenderMessageId") ?? 0;
            messageDate = GetNullableInt(offerDoc, "CreatedAt") ?? now;
            offerAction = BuildOfferActionFromDocument(offerDoc);
        }

        if (sellerId <= 0 || sellerId == buyerId)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        if (sellerMessageId <= 0)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        var status = GetNullableString(offerDoc, "Status") ?? "Pending";
        if (!string.Equals(status, "Pending", StringComparison.OrdinalIgnoreCase))
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        var expiresAt = GetNullableInt(offerDoc, "ExpiresAt") ?? offerAction!.ExpiresAt;
        var isExpired = expiresAt > 0 && now > expiresAt;
        if (isExpired)
        {
            await MarkOfferResolvedAsync(offersCollection, offerDoc, "Expired", buyerId, now, null);
            var expiredAction = BuildResolvedAction(offerAction!, accepted: false, declined: true);
            return await SendResolvedUpdatesAsync(
                input,
                buyerId,
                sellerId,
                obj.OfferMsgId,
                sellerMessageId,
                messageDate,
                expiredAction,
                buyerBalance: null,
                sellerBalance: null,
                now);
        }

        if (obj.Decline)
        {
            await MarkOfferResolvedAsync(offersCollection, offerDoc, "Declined", buyerId, now, null);
            var declinedAction = BuildResolvedAction(offerAction!, accepted: false, declined: true);
            return await SendResolvedUpdatesAsync(
                input,
                buyerId,
                sellerId,
                obj.OfferMsgId,
                sellerMessageId,
                messageDate,
                declinedAction,
                buyerBalance: null,
                sellerBalance: null,
                now);
        }

        var price = offerAction!.Price as TStarsAmount;
        if (price == null || price.Amount <= 0 || price.Nanos != 0)
        {
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
        }

        var savedGiftsCollection = mongoDatabase.GetCollection<BsonDocument>(SavedGiftsCollectionName);
        var giftsCollection = mongoDatabase.GetCollection<BsonDocument>(GiftsCollectionName);
        var balanceCollection = mongoDatabase.GetCollection<BsonDocument>(BalanceCollectionName);

        var savedGiftId = GetNullableLong(offerDoc, "GiftSavedId") ?? 0;
        var offerSlug = GetNullableString(offerDoc, "Slug");

        BsonDocument? savedGiftDoc = null;
        if (savedGiftId > 0)
        {
            var savedGiftFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("OwnerUserId", sellerId),
                Builders<BsonDocument>.Filter.Eq("SavedId", savedGiftId)
            );
            savedGiftDoc = await savedGiftsCollection.Find(savedGiftFilter).FirstOrDefaultAsync();
        }

        if (savedGiftDoc == null && !string.IsNullOrWhiteSpace(offerSlug))
        {
            var savedGiftBySlugFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("OwnerUserId", sellerId),
                Builders<BsonDocument>.Filter.Eq("Slug", offerSlug)
            );
            savedGiftDoc = await savedGiftsCollection.Find(savedGiftBySlugFilter).FirstOrDefaultAsync();
        }

        if (savedGiftDoc == null)
        {
            RpcErrors.RpcErrors400.StargiftNotFound.ThrowRpcError();
        }

        if (GetLong(savedGiftDoc!, "OwnerUserId") != sellerId)
        {
            RpcErrors.RpcErrors400.StargiftOwnerInvalid.ThrowRpcError();
        }

        if (!savedGiftDoc!.GetValue("Upgraded", false).AsBoolean ||
            savedGiftDoc.GetValue("Converted", false).AsBoolean ||
            savedGiftDoc.GetValue("Refunded", false).AsBoolean)
        {
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
        }

        var buyerBalanceFilter = Builders<BsonDocument>.Filter.Eq("UserId", buyerId);
        var sellerBalanceFilter = Builders<BsonDocument>.Filter.Eq("UserId", sellerId);
        var buyerBalanceDoc = await balanceCollection.Find(buyerBalanceFilter).FirstOrDefaultAsync();
        var sellerBalanceDoc = await balanceCollection.Find(sellerBalanceFilter).FirstOrDefaultAsync();

        var buyerBalance = buyerBalanceDoc != null ? GetLong(buyerBalanceDoc, "Balance") : 0;
        var sellerBalance = sellerBalanceDoc != null ? GetLong(sellerBalanceDoc, "Balance") : 0;
        if (buyerBalance < price!.Amount)
        {
            RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();
        }

        var newBuyerBalance = buyerBalance - price.Amount;
        var newSellerBalance = sellerBalance + price.Amount;

        if (buyerBalanceDoc == null)
        {
            await balanceCollection.InsertOneAsync(new BsonDocument
            {
                { "UserId", buyerId },
                { "Balance", newBuyerBalance },
                { "LastUpdated", DateTime.UtcNow }
            });
        }
        else
        {
            await balanceCollection.UpdateOneAsync(
                buyerBalanceFilter,
                Builders<BsonDocument>.Update
                    .Set("Balance", newBuyerBalance)
                    .Set("LastUpdated", DateTime.UtcNow));
        }

        if (sellerBalanceDoc == null)
        {
            await balanceCollection.InsertOneAsync(new BsonDocument
            {
                { "UserId", sellerId },
                { "Balance", newSellerBalance },
                { "LastUpdated", DateTime.UtcNow }
            });
        }
        else
        {
            await balanceCollection.UpdateOneAsync(
                sellerBalanceFilter,
                Builders<BsonDocument>.Update
                    .Set("Balance", newSellerBalance)
                    .Set("LastUpdated", DateTime.UtcNow));
        }

        var newSavedId = await idGenerator.NextIdAsync(IdType.SavedStarGiftId, buyerId);
        await savedGiftsCollection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", savedGiftDoc["_id"]),
            Builders<BsonDocument>.Update
                .Set("OwnerUserId", buyerId)
                .Set("SavedId", newSavedId)
                .Set("TransferredFrom", sellerId)
                .Set("TransferDate", now)
                .Set("CanTransferAt", now)
                .Set("Saved", false)
                .Set("PinnedToTop", false)
                .Unset("PinnedOrder")
                .Unset(StarGiftResaleHelper.ResaleStarsAmountField)
                .Unset(StarGiftResaleHelper.ResaleStarsNanosField)
                .Set(StarGiftResaleHelper.ResaleUpdatedAtField, now));

        var giftId = GetLong(savedGiftDoc, "GiftId");
        var giftDoc = await giftsCollection.Find(Builders<BsonDocument>.Filter.Eq("GiftId", giftId)).FirstOrDefaultAsync();
        var giftTitle = giftDoc != null ? GetNullableString(giftDoc, "Title") : null;

        var buyerTxDoc = StarsTransactionStore.CreateTransactionDocument(
            buyerId,
            -price.Amount,
            now,
            (int)PeerType.User,
            sellerId,
            giftId: giftId,
            title: giftTitle ?? "Star Gift",
            description: "Star gift offer purchase",
            stargiftResale: true,
            offer: true);

        var sellerTxDoc = StarsTransactionStore.CreateTransactionDocument(
            sellerId,
            price.Amount,
            now,
            (int)PeerType.User,
            buyerId,
            giftId: giftId,
            title: giftTitle ?? "Star Gift",
            description: "Star gift offer sale",
            stargiftResale: true,
            offer: true);

        var txCollection = StarsTransactionStore.GetCollection(mongoDatabase);
        await txCollection.InsertOneAsync(buyerTxDoc);
        await txCollection.InsertOneAsync(sellerTxDoc);

        await MarkOfferResolvedAsync(offersCollection, offerDoc, "Accepted", buyerId, now, buyerId);

        var acceptedAction = BuildResolvedAction(offerAction, accepted: true, declined: false);
        return await SendResolvedUpdatesAsync(
            input,
            buyerId,
            sellerId,
            obj.OfferMsgId,
            sellerMessageId,
            messageDate,
            acceptedAction,
            buyerBalance: newBuyerBalance,
            sellerBalance: newSellerBalance,
            now);
    }

    private async Task<TUpdates> SendResolvedUpdatesAsync(
        IRequestInput input,
        long buyerId,
        long sellerId,
        int buyerMessageId,
        int sellerMessageId,
        int originalDate,
        TMessageActionStarGiftPurchaseOffer resolvedAction,
        long? buyerBalance,
        long? sellerBalance,
        int now)
    {
        var buyerPts = await idGenerator.NextIdAsync(IdType.Pts, buyerId);
        var sellerPts = await idGenerator.NextIdAsync(IdType.Pts, sellerId);
        var messageDate = originalDate > 0 ? originalDate : now;

        var users = await BuildUsersAsync(input, buyerId, sellerId);

        var buyerUpdates = new TVector<IUpdate>
        {
            new TUpdateEditMessage
            {
                Message = new TMessageService
                {
                    Id = buyerMessageId,
                    FromId = new TPeerUser { UserId = sellerId },
                    PeerId = new TPeerUser { UserId = sellerId },
                    Date = messageDate,
                    Out = false,
                    Action = resolvedAction
                },
                Pts = buyerPts,
                PtsCount = 1
            }
        };

        if (buyerBalance.HasValue)
        {
            buyerUpdates.Add(new TUpdateStarsBalance
            {
                Balance = new TStarsAmount { Amount = buyerBalance.Value }
            });
        }

        var sellerUpdates = new TVector<IUpdate>
        {
            new TUpdateEditMessage
            {
                Message = new TMessageService
                {
                    Id = sellerMessageId,
                    FromId = new TPeerUser { UserId = sellerId },
                    PeerId = new TPeerUser { UserId = buyerId },
                    Date = messageDate,
                    Out = true,
                    Action = resolvedAction
                },
                Pts = sellerPts,
                PtsCount = 1
            }
        };

        if (sellerBalance.HasValue)
        {
            sellerUpdates.Add(new TUpdateStarsBalance
            {
                Balance = new TStarsAmount { Amount = sellerBalance.Value }
            });
        }

        var buyerUpdatesObject = new TUpdates
        {
            Updates = buyerUpdates,
            Users = users,
            Chats = [],
            Date = now,
            Seq = 0
        };

        var sellerUpdatesObject = new TUpdates
        {
            Updates = sellerUpdates,
            Users = users,
            Chats = [],
            Date = now,
            Seq = 0
        };

        await objectMessageSender.PushMessageToPeerAsync(
            new Peer(PeerType.User, sellerId),
            sellerUpdatesObject,
            excludeUserId: buyerId,
            pts: sellerPts);

        await objectMessageSender.PushMessageToPeerAsync(
            new Peer(PeerType.User, buyerId),
            buyerUpdatesObject,
            excludeAuthKeyId: input.PermAuthKeyId,
            pts: buyerPts);

        return buyerUpdatesObject;
    }

    private async Task<TVector<IUser>> BuildUsersAsync(IRequestInput input, long buyerId, long sellerId)
    {
        var userList = await userConverterService.GetUserListAsync(input, [buyerId, sellerId], true, true, input.Layer);
        var users = new TVector<IUser>();
        foreach (var user in userList)
        {
            users.Add(user);
        }

        return users;
    }

    private static TMessageActionStarGiftPurchaseOffer BuildResolvedAction(
        TMessageActionStarGiftPurchaseOffer source,
        bool accepted,
        bool declined)
    {
        return new TMessageActionStarGiftPurchaseOffer
        {
            Accepted = accepted,
            Declined = declined,
            Gift = source.Gift,
            Price = source.Price,
            ExpiresAt = source.ExpiresAt
        };
    }

    private static TMessageActionStarGiftPurchaseOffer BuildOfferActionFromDocument(BsonDocument offerDoc)
    {
        var giftSavedId = GetNullableLong(offerDoc, "GiftSavedId") ?? 0;
        var giftId = GetNullableLong(offerDoc, "GiftId") ?? 0;
        var sellerId = GetNullableLong(offerDoc, "SenderUserId") ?? 0;
        var priceAmount = GetNullableLong(offerDoc, "PriceAmount") ?? 0;
        var priceNanos = GetNullableInt(offerDoc, "PriceNanos") ?? 0;
        var expiresAt = GetNullableInt(offerDoc, "ExpiresAt") ?? 0;
        var slug = GetNullableString(offerDoc, "Slug") ?? string.Empty;

        return new TMessageActionStarGiftPurchaseOffer
        {
            Accepted = false,
            Declined = false,
            Gift = new TStarGiftUnique
            {
                Id = giftSavedId,
                GiftId = giftId,
                Title = "Collectible Gift",
                Slug = slug,
                Num = 1,
                OwnerId = new TPeerUser { UserId = sellerId },
                Attributes = []
            },
            Price = new TStarsAmount
            {
                Amount = priceAmount,
                Nanos = priceNanos
            },
            ExpiresAt = expiresAt
        };
    }

    private static async Task<BsonDocument> CreateOfferFromMessageAsync(
        IMongoCollection<BsonDocument> offersCollection,
        TMessageActionStarGiftPurchaseOffer offerAction,
        long sellerId,
        long buyerId,
        int senderMessageId,
        int now)
    {
        var starsPrice = offerAction.Price as TStarsAmount ?? new TStarsAmount { Amount = 0, Nanos = 0 };
        var giftUnique = offerAction.Gift as TStarGiftUnique;

        var offerDoc = new BsonDocument
        {
            { "SenderUserId", sellerId },
            { "RecipientUserId", buyerId },
            { "SenderMessageId", senderMessageId },
            { "GiftSavedId", giftUnique != null ? giftUnique.Id : 0L },
            { "GiftId", giftUnique != null ? giftUnique.GiftId : 0L },
            { "Slug", giftUnique?.Slug ?? string.Empty },
            { "PriceAmount", starsPrice.Amount },
            { "PriceNanos", starsPrice.Nanos },
            { "Duration", Math.Max(0, offerAction.ExpiresAt - now) },
            { "ExpiresAt", offerAction.ExpiresAt },
            { "AllowPaidStars", BsonNull.Value },
            { "Status", "Pending" },
            { "CreatedAt", now },
            { "ResolvedAt", BsonNull.Value },
            { "ResolvedByUserId", BsonNull.Value },
            { "BuyerUserId", BsonNull.Value }
        };

        await offersCollection.InsertOneAsync(offerDoc);
        return offerDoc;
    }

    private static async Task MarkOfferResolvedAsync(
        IMongoCollection<BsonDocument> offersCollection,
        BsonDocument offerDoc,
        string status,
        long resolvedByUserId,
        int now,
        long? buyerUserId)
    {
        var update = Builders<BsonDocument>.Update
            .Set("Status", status)
            .Set("ResolvedAt", now)
            .Set("ResolvedByUserId", resolvedByUserId);

        if (buyerUserId.HasValue)
        {
            update = update.Set("BuyerUserId", buyerUserId.Value);
        }
        else
        {
            update = update.Set("BuyerUserId", BsonNull.Value);
        }

        await offersCollection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", offerDoc["_id"]),
            update);
    }

    private static long GetLong(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull)
        {
            return 0;
        }

        return doc[field].IsInt64 ? doc[field].AsInt64 : doc[field].AsInt32;
    }

    private static long? GetNullableLong(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull)
        {
            return null;
        }

        return doc[field].IsInt64 ? doc[field].AsInt64 : doc[field].AsInt32;
    }

    private static int? GetNullableInt(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull)
        {
            return null;
        }

        return doc[field].IsInt32 ? doc[field].AsInt32 : (int)doc[field].AsInt64;
    }

    private static string? GetNullableString(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull)
        {
            return null;
        }

        return doc[field].AsString;
    }
}

