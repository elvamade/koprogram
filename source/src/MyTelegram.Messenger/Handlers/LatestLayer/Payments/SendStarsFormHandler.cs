using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Services.StarsTransactions;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Make a payment using <a href="https://corefork.telegram.org/api/stars#using-stars">Telegram Stars, see here »</a> for more info.
/// Possible errors
/// Code Type Description
/// 406 API_GIFT_RESTRICTED_UPDATE_APP Please update the app to access the gift API.
/// 400 BALANCE_TOO_LOW The transaction cannot be completed because the current <a href="https://corefork.telegram.org/api/stars">Telegram Stars balance</a> is too low.
/// 403 BOT_ACCESS_FORBIDDEN The specified method <em>can</em> be used over a <a href="https://corefork.telegram.org/api/bots/connected-business-bots">business connection</a> for some operations, but the specified query attempted an operation that is not allowed over a business connection.
/// 400 BOT_INVOICE_INVALID The specified invoice is invalid.
/// 400 BUSINESS_CONNECTION_INVALID The <code>connection_id</code> passed to the wrapping <a href="https://corefork.telegram.org/api/business">invokeWithBusinessConnection</a> call is invalid.
/// 400 FORM_EXPIRED The form was generated more than 10 minutes ago and has expired, please re-generate it using <a href="https://corefork.telegram.org/method/payments.getPaymentForm">payments.getPaymentForm</a> and pass the new <code>form_id</code>.
/// 400 FORM_ID_EMPTY The specified form ID is empty.
/// 400 FORM_SUBMIT_DUPLICATE The same payment form was already submitted.  .
/// 400 FORM_UNSUPPORTED Please update your client.
/// 400 GIFT_STARS_INVALID The specified amount of stars is invalid.
/// 400 MEDIA_ALREADY_PAID You already paid for the specified media.
/// 400 MONTH_INVALID The number of months specified in inputInvoicePremiumGiftStars.months is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 406 PRECHECKOUT_FAILED Precheckout failed, a detailed and localized description for the error will be emitted via an <a href="https://corefork.telegram.org/api/errors#406-not-acceptable">updateServiceNotification as specified here »</a>.
/// 400 PURPOSE_INVALID The specified payment purpose is invalid.
/// 400 STARGIFT_ALREADY_UPGRADED The specified gift was already upgraded to a collectible gift.
/// 400 STARGIFT_NOT_FOUND The specified gift was not found.
/// 400 STARGIFT_OWNER_INVALID You cannot transfer or sell a gift owned by another user.
/// 400 STARGIFT_SLUG_INVALID The specified gift slug is invalid.
/// 400 STARGIFT_USAGE_LIMITED The gift is sold out.
/// 400 STARGIFT_USER_USAGE_LIMITED You've reached the starGift.limited_per_user limit, you can't buy any more gifts of this type.
/// 406 STARS_FORM_AMOUNT_MISMATCH The form amount has changed, please fetch the new form using <a href="https://corefork.telegram.org/method/payments.getPaymentForm">payments.getPaymentForm</a> and restart the process.
/// 400 TO_ID_INVALID The specified <code>to_id</code> of the passed inputInvoiceStarGiftResale or inputInvoiceStarGiftTransfer is invalid.
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.sendStarsForm"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class SendStarsFormHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IIdGenerator idGenerator,
    IQueryProcessor queryProcessor,
    IChannelAppService channelAppService,
    IUserConverterService userConverterService,
    ICommandBus commandBus,
    IPrivacyAppService privacyAppService,
    IObjectMessageSender objectMessageSender) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestSendStarsForm, MyTelegram.Schema.Payments.IPaymentResult>
{
    protected override async Task<MyTelegram.Schema.Payments.IPaymentResult> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestSendStarsForm obj)
    {
        if (obj.Invoice is TInputInvoiceStarGift starGiftInvoice)
        {
            return await HandleStarGiftPurchaseAsync(input, obj.FormId, starGiftInvoice);
        }

        if (obj.Invoice is TInputInvoiceStarGiftResale resaleInvoice)
        {
            return await HandleStarGiftResalePaymentAsync(input, obj.FormId, resaleInvoice);
        }

        if (obj.Invoice is TInputInvoiceStarGiftAuctionBid auctionBidInvoice)
        {
            return await HandleAuctionBidAsync(input, obj.FormId, auctionBidInvoice);
        }

        if (obj.Invoice is TInputInvoiceStarGiftUpgrade upgradeInvoice)
        {
            return await HandleStarGiftUpgradePaymentAsync(input, obj.FormId, upgradeInvoice);
        }

        RpcErrors.RpcErrors400.StarsInvoiceInvalid.ThrowRpcError();
        throw new RpcException(new RpcError(400, "STARS_INVOICE_INVALID"));
    }

    private async Task<MyTelegram.Schema.Payments.IPaymentResult> HandleStarGiftPurchaseAsync(
        IRequestInput input,
        long formId,
        TInputInvoiceStarGift invoice)
    {
        var senderId = input.UserId;
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (formId == 0)
        {
            RpcErrors.RpcErrors400.FormIdEmpty.ThrowRpcError();
        }

        const int HideNameFlag = 1 << 0;
        const int MessageFlag = 1 << 1;
        const int IncludeUpgradeFlag = 1 << 2;

        var hideName = (invoice.Flags & HideNameFlag) != 0;
        var includeUpgrade = (invoice.Flags & IncludeUpgradeFlag) != 0;
        var message = (invoice.Flags & MessageFlag) != 0 ? invoice.Message : null;

        // Get the gift from database
        var giftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stargiftreadmodel");
        var giftFilter = Builders<BsonDocument>.Filter.Eq("GiftId", invoice.GiftId);
        var giftDoc = await giftsCollection.Find(giftFilter).FirstOrDefaultAsync();

        if (giftDoc == null)
        {
            RpcErrors.RpcErrors400.StargiftNotFound.ThrowRpcError();
        }

        // Check if gift is sold out
        if (giftDoc!.GetValue("SoldOut", false).AsBoolean)
        {
            RpcErrors.RpcErrors400.StargiftUsageLimited.ThrowRpcError();
        }

        if (giftDoc.GetValue("Auction", false).AsBoolean)
        {
            // Desktop may still send inputInvoiceStarGift for auction items.
            // Convert to an auction bid with minimum allowed amount.
            if (input.DeviceType == DeviceType.Desktop)
            {
                var minBidAmount = GetNullableLong(giftDoc, "MinBidAmount") ?? 100;
                var auctionInvoice = new TInputInvoiceStarGiftAuctionBid
                {
                    GiftId = invoice.GiftId,
                    BidAmount = minBidAmount,
                    UpdateBid = false,
                    HideName = hideName,
                    Message = message,
                    Peer = invoice.Peer
                };
                return await HandleAuctionBidAsync(input, formId, auctionInvoice);
            }

            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
        }

        var lockedUntilDate = GetNullableInt(giftDoc, "LockedUntilDate");
        if (lockedUntilDate.HasValue && now < lockedUntilDate.Value)
        {
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
        }

        if (giftDoc.GetValue("Limited", false).AsBoolean)
        {
            var remains = GetNullableInt(giftDoc, "AvailabilityRemains");
            if (remains.HasValue && remains.Value <= 0)
            {
                RpcErrors.RpcErrors400.StargiftUsageLimited.ThrowRpcError();
            }
        }

        if (giftDoc.GetValue("RequirePremium", false).AsBoolean)
        {
            var senderUser = await queryProcessor.ProcessAsync(new GetUserByIdQuery(senderId));
            if (senderUser == null)
            {
                RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
            }
            if (!senderUser.Premium)
            {
                RpcErrors.RpcErrors400.PremiumAccountRequired.ThrowRpcError();
            }
        }

        var savedGiftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-savedstargiftreadmodel");

        // Get gift price
        var stars = giftDoc["Stars"].IsInt64 ? giftDoc["Stars"].AsInt64 : giftDoc["Stars"].AsInt32;
        var convertStars = giftDoc["ConvertStars"].IsInt64 ? giftDoc["ConvertStars"].AsInt64 : giftDoc["ConvertStars"].AsInt32;
        
        // Calculate total price (gift + optional prepaid upgrade).
        long totalPrice = stars;
        var canUpgrade = StarGiftUpgradeStateHelper.IsUpgradableGift(giftDoc);
        long? upgradeStars = null;
        if (includeUpgrade)
        {
            upgradeStars = GetNullableLong(giftDoc, "UpgradeStars");
            if (!upgradeStars.HasValue)
            {
                RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
            }

            if (upgradeStars.Value > 0)
            {
                totalPrice += upgradeStars.Value;
            }
            else
            {
                // Free-upgrade gifts don't need prepaid upgrade payment.
                includeUpgrade = false;
            }
        }

        // Get recipient peer (allow Self for self-gifting)
        var recipientPeer = peerHelper.GetPeer(invoice.Peer, input.UserId);
        if (recipientPeer.PeerType != PeerType.User &&
            recipientPeer.PeerType != PeerType.Self &&
            recipientPeer.PeerType != PeerType.Channel)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var recipientOwnerId = recipientPeer.PeerId;
        IChannelReadModel? recipientChannel = null;
        if (recipientPeer.PeerType == PeerType.Channel)
        {
            recipientChannel = await channelAppService.GetAsync(recipientOwnerId);
            if (recipientChannel == null)
            {
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            }
        }
        else if (await queryProcessor.ProcessAsync(new GetUserByIdQuery(recipientOwnerId)) == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        // Check sender's balance
        var balanceCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-userstarsbalancereadmodel");
        var balanceFilter = Builders<BsonDocument>.Filter.Eq("UserId", senderId);
        var balanceDoc = await balanceCollection.Find(balanceFilter).FirstOrDefaultAsync();

        long currentBalance = 0;
        if (balanceDoc != null && balanceDoc.Contains("Balance"))
        {
            currentBalance = balanceDoc["Balance"].IsInt64 ? balanceDoc["Balance"].AsInt64 : balanceDoc["Balance"].AsInt32;
        }

        if (currentBalance < totalPrice)
        {
            RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();
        }

        // Deduct stars from sender's balance (gift price + upgrade if applicable)
        var newBalance = currentBalance - totalPrice;
        if (balanceDoc != null)
        {
            var updateBalanceDoc = Builders<BsonDocument>.Update
                .Set("Balance", newBalance)
                .Set("LastUpdated", DateTime.UtcNow);
            await balanceCollection.UpdateOneAsync(balanceFilter, updateBalanceDoc);
        }
        else
        {
            var newBalanceDoc = new BsonDocument
            {
                { "UserId", senderId },
                { "Balance", newBalance },
                { "LastUpdated", DateTime.UtcNow }
            };
            await balanceCollection.InsertOneAsync(newBalanceDoc);
        }

        // Update gift availability if limited
        if (giftDoc.GetValue("Limited", false).AsBoolean)
        {
            var remainsValue = giftDoc.Contains("AvailabilityRemains") && !giftDoc["AvailabilityRemains"].IsBsonNull
                ? (giftDoc["AvailabilityRemains"].IsInt32 ? giftDoc["AvailabilityRemains"].AsInt32 : (int)giftDoc["AvailabilityRemains"].AsInt64)
                : 0;

            if (remainsValue > 0)
            {
                var newRemains = remainsValue - 1;
                var updateGift = Builders<BsonDocument>.Update
                    .Set("AvailabilityRemains", newRemains)
                    .Set("LastSaleDate", now);
                
                // Set FirstSaleDate only on first purchase (when it's not set yet)
                var hasFirstSaleDate = giftDoc.Contains("FirstSaleDate") && !giftDoc["FirstSaleDate"].IsBsonNull;
                if (!hasFirstSaleDate)
                {
                    updateGift = updateGift.Set("FirstSaleDate", now);
                }
                
                if (newRemains == 0)
                {
                    updateGift = updateGift.Set("SoldOut", true);
                }
                await giftsCollection.UpdateOneAsync(giftFilter, updateGift);
            }
        }

        // Get sticker for the gift
        var stickerId = giftDoc["StickerId"].IsInt64 ? giftDoc["StickerId"].AsInt64 : giftDoc["StickerId"].AsInt32;
        var documentsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");
        var docFilter = Builders<BsonDocument>.Filter.Eq("DocumentId", stickerId);
        var stickerDoc = await documentsCollection.Find(docFilter).FirstOrDefaultAsync();

        IDocument sticker;
        if (stickerDoc != null)
        {
            sticker = ConvertDocument(stickerDoc);
        }
        else
        {
            sticker = new TDocumentEmpty { Id = stickerId };
        }

        // Build TStarGift object
        // starGift.upgrade_stars always shows the upgrade cost from gift definition
        // This is where client looks to know the upgrade price
        var tGift = new TStarGift
        {
            Id = invoice.GiftId,
            Limited = giftDoc.GetValue("Limited", false).AsBoolean,
            SoldOut = giftDoc.GetValue("SoldOut", false).AsBoolean,
            Birthday = giftDoc.GetValue("Birthday", false).AsBoolean,
            RequirePremium = giftDoc.GetValue("RequirePremium", false).AsBoolean,
            LimitedPerUser = giftDoc.GetValue("LimitedPerUser", false).AsBoolean,
            Sticker = sticker,
            Stars = stars,
            ConvertStars = convertStars,
            AvailabilityRemains = GetNullableInt(giftDoc, "AvailabilityRemains"),
            AvailabilityTotal = GetNullableInt(giftDoc, "AvailabilityTotal"),
            AvailabilityResale = GetNullableLong(giftDoc, "AvailabilityResale"),
            FirstSaleDate = GetNullableInt(giftDoc, "FirstSaleDate"),
            LastSaleDate = GetNullableInt(giftDoc, "LastSaleDate"),
            // Always include upgrade cost - client needs this to know upgrade price
            UpgradeStars = GetNullableLong(giftDoc, "UpgradeStars"),
            ResellMinStars = GetNullableLong(giftDoc, "ResellMinStars"),
            Title = GetNullableString(giftDoc, "Title")
        };

        // Generate message ID and pts in owner's history
        var messageOwnerPeerId = recipientPeer.PeerType == PeerType.Channel ? recipientOwnerId : senderId;
        var senderMessageId = await idGenerator.NextIdAsync(IdType.MessageId, messageOwnerPeerId);

        // Channels don't use user privacy settings for gift auto-save.
        var autoSaveGift = recipientPeer.PeerType == PeerType.Channel ||
                           await CheckAutoSaveGiftPrivacyAsync(senderId, recipientOwnerId);

        // Save the gift to recipient's saved gifts
        var savedGiftId = await idGenerator.NextIdAsync(IdType.SavedStarGiftId, recipientOwnerId);

        var savedGiftDoc = new BsonDocument
        {
            { "SavedId", (long)savedGiftId },
            { "OwnerUserId", recipientOwnerId },
            { "SenderUserId", senderId },
            { "FromUserId", hideName ? BsonNull.Value : senderId },
            { "GiftId", invoice.GiftId },
            { "Date", now },
            { "MsgId", senderMessageId },
            { "NameHidden", hideName },
            { "Saved", autoSaveGift },
            { "PinnedToTop", false },
            { "Converted", false },
            { "Upgraded", false },
            { "Refunded", false },
            { "UpgradeSeparate", includeUpgrade },
            { "PrepaidUpgrade", includeUpgrade },
            { "CanUpgrade", canUpgrade },
            { "ConvertStars", convertStars },
            { "UpgradeStars", includeUpgrade && upgradeStars.HasValue ? upgradeStars.Value : BsonNull.Value },
            { "PrepaidUpgradeHash", BsonNull.Value },
            { "Message", message != null ? message.Text : BsonNull.Value },
            { "MessageEntities", BsonNull.Value }
        };
        await savedGiftsCollection.InsertOneAsync(savedGiftDoc);
        var senderPts = await idGenerator.NextIdAsync(IdType.Pts, messageOwnerPeerId);
        var randomId = Random.Shared.NextInt64();

        // Build messageActionStarGift
        // messageActionStarGift.upgrade_stars shows the upgrade cost if gift can be upgraded
        // This is different from savedStarGift.upgrade_stars which only shows amount if prepaid
        var messageAction = new TMessageActionStarGift
        {
            NameHidden = hideName,
            Saved = autoSaveGift,
            Converted = false,
            Upgraded = false,
            Refunded = false,
            CanUpgrade = canUpgrade,
            PrepaidUpgrade = includeUpgrade,
            Gift = tGift,
            Message = message,
            ConvertStars = convertStars,
            // messageActionStarGift.upgrade_stars shows upgrade cost if can_upgrade is true
            UpgradeStars = canUpgrade ? (includeUpgrade ? upgradeStars : GetNullableLong(giftDoc, "UpgradeStars")) : null,
            FromId = hideName ? null : new TPeerUser { UserId = senderId },
            Peer = recipientPeer.PeerType == PeerType.Channel
                ? new TPeerChannel { ChannelId = recipientOwnerId }
                : new TPeerUser { UserId = recipientOwnerId },
            SavedId = savedGiftId,
            PrepaidUpgradeHash = null
        };

        var giftTitle = GetNullableString(giftDoc, "Title") ?? "Star Gift";
        var transactionPeerType = recipientPeer.PeerType == PeerType.Channel ? PeerType.Channel : PeerType.User;
        var transactionDoc = StarsTransactionStore.CreateTransactionDocument(
            senderId,
            -totalPrice,
            now,
            (int)transactionPeerType,
            recipientOwnerId,
            giftId: invoice.GiftId,
            title: giftTitle,
            description: "Star gift purchase",
            gift: true,
            stargiftPrepaidUpgrade: false
        );
        await StarsTransactionStore.GetCollection(mongoDatabase).InsertOneAsync(transactionDoc);

        // Create service message for sender (outbox)
        var senderMessage = new TMessageService
        {
            Id = senderMessageId,
            FromId = new TPeerUser { UserId = senderId },
            PeerId = recipientPeer.PeerType == PeerType.Channel
                ? new TPeerChannel { ChannelId = recipientOwnerId }
                : new TPeerUser { UserId = recipientOwnerId },
            Date = now,
            Out = true,
            Post = recipientPeer.PeerType == PeerType.Channel && (recipientChannel?.Broadcast ?? false),
            Action = messageAction
        };

        // Create update for sender
        var updateMessageId = new TUpdateMessageID { Id = senderMessageId, RandomId = randomId };
        var updateStarsBalance = new TUpdateStarsBalance
        {
            Balance = new TStarsAmount { Amount = newBalance }
        };

        var senderUpdates = new TVector<IUpdate>();
        senderUpdates.Add(updateMessageId);
        if (recipientPeer.PeerType == PeerType.Channel)
        {
            senderUpdates.Add(new TUpdateReadChannelInbox
            {
                ChannelId = recipientOwnerId,
                MaxId = senderMessageId,
                Pts = senderPts,
                StillUnreadCount = 0
            });
            senderUpdates.Add(new TUpdateNewChannelMessage
            {
                Message = senderMessage,
                Pts = senderPts,
                PtsCount = 1
            });
        }
        else
        {
            senderUpdates.Add(new TUpdateNewMessage
            {
                Message = senderMessage,
                Pts = senderPts,
                PtsCount = 1
            });
        }
        senderUpdates.Add(updateStarsBalance);

        // Send message to recipient via command bus (creates inbox message for recipient)
        var ownerPeer = recipientPeer.PeerType == PeerType.Channel
            ? recipientOwnerId.ToChannelPeer()
            : senderId.ToUserPeer();
        var toPeer = recipientPeer.PeerType == PeerType.Channel
            ? recipientOwnerId.ToChannelPeer()
            : recipientOwnerId.ToUserPeer();
        var senderPeer = senderId.ToUserPeer();

        var messageItem = new MessageItem(
            ownerPeer,
            toPeer,
            senderPeer,
            senderId,
            senderMessageId,
            string.Empty,
            now,
            randomId,
            true,
            SendMessageType.MessageService,
            MessageType.Text,
            MessageSubType.StarGiftPurchase,
            MessageAction: messageAction,
            MessageActionType: MessageActionType.StarGift,
            Pts: senderPts,
            Post: recipientPeer.PeerType == PeerType.Channel && (recipientChannel?.Broadcast ?? false)
        );

        // Keep original ReqMsgId, channel/user event handlers skip duplicate RPC for StarGiftPurchase.
        var command = new StartSendMessageCommand(
            TempId.New,
            input.ToRequestInfo(),
            [new SendMessageItem(messageItem)]
        );

        await commandBus.PublishAsync(command);

        // Get sender and recipient user info via converter service (handles privacy properly)
        var userIdsToFetch = new List<long> { senderId };
        if (recipientPeer.PeerType != PeerType.Channel && recipientOwnerId != senderId)
        {
            userIdsToFetch.Add(recipientOwnerId);
        }
        var userList = await userConverterService.GetUserListAsync(input, userIdsToFetch, true, true, input.Layer);
        var users = new TVector<IUser>();
        foreach (var user in userList)
        {
            users.Add(user);
        }

        // Return PaymentResult with Updates for sender
        return new MyTelegram.Schema.Payments.TPaymentResult
        {
            Updates = new TUpdates
            {
                Updates = senderUpdates,
                Users = users,
                Chats = [],
                Date = now,
                Seq = 0
            }
        };
    }

    private async Task<MyTelegram.Schema.Payments.IPaymentResult> HandleStarGiftResalePaymentAsync(
        IRequestInput input,
        long formId,
        TInputInvoiceStarGiftResale invoice)
    {
        if (formId == 0)
        {
            RpcErrors.RpcErrors400.FormIdEmpty.ThrowRpcError();
        }

        if (invoice.Ton)
        {
            RpcErrors.RpcErrors400.StargiftResellCurrencyNotAllowed.ThrowRpcError();
        }

        var buyerId = input.UserId;
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var savedGiftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-savedstargiftreadmodel");
        var giftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stargiftreadmodel");
        var balanceCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-userstarsbalancereadmodel");

        var recipientPeer = peerHelper.GetPeer(invoice.ToId, buyerId);
        if (recipientPeer.PeerType != PeerType.User &&
            recipientPeer.PeerType != PeerType.Self &&
            recipientPeer.PeerType != PeerType.Channel)
        {
            RpcErrors.RpcErrors400.ToIdInvalid.ThrowRpcError();
        }

        if (recipientPeer.PeerType == PeerType.Channel)
        {
            var channel = await channelAppService.GetAsync(recipientPeer.PeerId);
            if (channel == null)
            {
                RpcErrors.RpcErrors400.ToIdInvalid.ThrowRpcError();
            }
        }
        else
        {
            var user = await queryProcessor.ProcessAsync(new GetUserByIdQuery(recipientPeer.PeerId));
            if (user == null)
            {
                RpcErrors.RpcErrors400.ToIdInvalid.ThrowRpcError();
            }
        }

        var resaleFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("Slug", invoice.Slug),
            Builders<BsonDocument>.Filter.Eq("Upgraded", true),
            Builders<BsonDocument>.Filter.Ne("Converted", true),
            Builders<BsonDocument>.Filter.Ne("Refunded", true)
        );
        var savedGiftDoc = await savedGiftsCollection.Find(resaleFilter).FirstOrDefaultAsync();
        if (savedGiftDoc == null)
        {
            RpcErrors.RpcErrors400.StargiftSlugInvalid.ThrowRpcError();
        }

        if (!StarGiftResaleHelper.TryGetResaleStarsAmount(savedGiftDoc!, out var resaleAmount, out _))
        {
            RpcErrors.RpcErrors400.StargiftNotFound.ThrowRpcError();
        }

        var sellerId = GetLong(savedGiftDoc, "OwnerUserId");
        if (sellerId == buyerId)
        {
            RpcErrors.RpcErrors400.StargiftOwnerInvalid.ThrowRpcError();
        }

        var buyerBalanceFilter = Builders<BsonDocument>.Filter.Eq("UserId", buyerId);
        var buyerBalanceDoc = await balanceCollection.Find(buyerBalanceFilter).FirstOrDefaultAsync();
        var buyerBalance = buyerBalanceDoc != null && buyerBalanceDoc.Contains("Balance")
            ? (buyerBalanceDoc["Balance"].IsInt64 ? buyerBalanceDoc["Balance"].AsInt64 : buyerBalanceDoc["Balance"].AsInt32)
            : 0;
        if (buyerBalance < resaleAmount)
        {
            RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();
        }

        var buyerNewBalance = buyerBalance - resaleAmount;
        await UpsertBalanceAsync(balanceCollection, buyerId, buyerNewBalance);

        var sellerBalanceFilter = Builders<BsonDocument>.Filter.Eq("UserId", sellerId);
        var sellerBalanceDoc = await balanceCollection.Find(sellerBalanceFilter).FirstOrDefaultAsync();
        var sellerBalance = sellerBalanceDoc != null && sellerBalanceDoc.Contains("Balance")
            ? (sellerBalanceDoc["Balance"].IsInt64 ? sellerBalanceDoc["Balance"].AsInt64 : sellerBalanceDoc["Balance"].AsInt32)
            : 0;
        var sellerNewBalance = sellerBalance + resaleAmount;
        await UpsertBalanceAsync(balanceCollection, sellerId, sellerNewBalance);

        var newOwnerId = recipientPeer.PeerId;
        var newSavedId = await idGenerator.NextIdAsync(IdType.SavedStarGiftId, newOwnerId);
        var transferUpdate = Builders<BsonDocument>.Update
            .Set("OwnerUserId", newOwnerId)
            .Set("SavedId", (long)newSavedId)
            .Set("TransferredFrom", sellerId)
            .Set("TransferDate", now)
            .Set("CanTransferAt", now)
            .Set("CanResellAt", now)
            .Set("Saved", false)
            .Set("PinnedToTop", false)
            .Unset("PinnedOrder")
            .Unset(StarGiftResaleHelper.ResaleStarsAmountField)
            .Unset(StarGiftResaleHelper.ResaleStarsNanosField)
            .Set(StarGiftResaleHelper.ResaleUpdatedAtField, now);
        await savedGiftsCollection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", savedGiftDoc["_id"]),
            transferUpdate);

        var giftId = GetLong(savedGiftDoc, "GiftId");
        await StarGiftResaleHelper.RecalculateGiftResaleStatsAsync(savedGiftsCollection, giftsCollection, giftId);

        var giftDoc = await giftsCollection.Find(Builders<BsonDocument>.Filter.Eq("GiftId", giftId)).FirstOrDefaultAsync();
        var giftTitle = GetNullableString(giftDoc ?? savedGiftDoc, "Title") ?? "Collectible Gift";

        var buyerTx = StarsTransactionStore.CreateTransactionDocument(
            buyerId,
            -resaleAmount,
            now,
            (int)PeerType.User,
            sellerId,
            giftId: giftId,
            title: giftTitle,
            description: "Gift resale purchase",
            stargiftResale: true
        );
        await StarsTransactionStore.GetCollection(mongoDatabase).InsertOneAsync(buyerTx);

        var sellerTx = StarsTransactionStore.CreateTransactionDocument(
            sellerId,
            resaleAmount,
            now,
            (int)PeerType.User,
            buyerId,
            giftId: giftId,
            title: giftTitle,
            description: "Gift resale sale",
            stargiftResale: true
        );
        await StarsTransactionStore.GetCollection(mongoDatabase).InsertOneAsync(sellerTx);

        var userIds = new HashSet<long> { buyerId, sellerId };
        if (recipientPeer.PeerType != PeerType.Channel)
        {
            userIds.Add(newOwnerId);
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

        return new MyTelegram.Schema.Payments.TPaymentResult
        {
            Updates = new TUpdates
            {
                Updates =
                [
                    new TUpdateStarsBalance
                    {
                        Balance = new TStarsAmount { Amount = buyerNewBalance }
                    }
                ],
                Users = users,
                Chats = [],
                Date = now,
                Seq = 0
            }
        };
    }

    private static async Task UpsertBalanceAsync(IMongoCollection<BsonDocument> balanceCollection, long userId, long balance)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", userId);
        var update = Builders<BsonDocument>.Update
            .Set("Balance", balance)
            .Set("LastUpdated", DateTime.UtcNow);

        await balanceCollection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
    }

    private static IDocument ConvertDocument(BsonDocument doc)
    {
        return new TDocument
        {
            Id = doc["DocumentId"].IsInt64 ? doc["DocumentId"].AsInt64 : doc["DocumentId"].AsInt32,
            AccessHash = doc["AccessHash"].IsInt64 ? doc["AccessHash"].AsInt64 : doc["AccessHash"].AsInt32,
            Date = doc["Date"].AsInt32,
            MimeType = doc["MimeType"].AsString,
            Size = doc["Size"].IsInt64 ? doc["Size"].AsInt64 : doc["Size"].AsInt32,
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

    private async Task<MyTelegram.Schema.Payments.IPaymentResult> HandleAuctionBidAsync(
        IRequestInput input,
        long formId,
        TInputInvoiceStarGiftAuctionBid invoice)
    {
        var userId = input.UserId;
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (formId == 0)
        {
            RpcErrors.RpcErrors400.FormIdEmpty.ThrowRpcError();
        }

        await StarGiftAuctionRoundProcessor.ProcessDueRoundsAsync(mongoDatabase, idGenerator, invoice.GiftId, now);

        // Get the auction gift from database
        var giftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stargiftreadmodel");
        var giftFilter = Builders<BsonDocument>.Filter.Eq("GiftId", invoice.GiftId);
        var giftDoc = await giftsCollection.Find(giftFilter).FirstOrDefaultAsync();

        if (giftDoc == null)
        {
            RpcErrors.RpcErrors400.StargiftNotFound.ThrowRpcError();
        }

        // Check if this is an auction gift
        if (!giftDoc!.GetValue("Auction", false).AsBoolean)
        {
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
        }

        // Check auction dates
        var auctionStartDate = GetNullableInt(giftDoc, "AuctionStartDate");
        var auctionEndDate = GetNullableInt(giftDoc, "AuctionEndDate");

        if (auctionStartDate.HasValue && now < auctionStartDate.Value)
        {
            throw new RpcException(new RpcError(400, "AUCTION_NOT_STARTED"));
        }

        if (auctionEndDate.HasValue && now > auctionEndDate.Value)
        {
            throw new RpcException(new RpcError(400, "AUCTION_ENDED"));
        }

        // Validate bid amount
        var minBidAmount = GetNullableLong(giftDoc, "MinBidAmount") ?? 100;
        if (invoice.BidAmount < minBidAmount)
        {
            throw new RpcException(new RpcError(400, "BID_AMOUNT_TOO_LOW"));
        }

        // Check existing bid
        var bidsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stargiftauctionbidreadmodel");
        var existingBidFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", userId),
            Builders<BsonDocument>.Filter.Eq("GiftId", invoice.GiftId),
            Builders<BsonDocument>.Filter.Eq("Returned", false)
        );
        var existingBid = await bidsCollection.Find(existingBidFilter).FirstOrDefaultAsync();

        long effectiveBidAmount = invoice.BidAmount;
        long previousBidAmount = 0;

        if (existingBid != null)
        {
            previousBidAmount = GetLong(existingBid, "BidAmount");
            if (invoice.UpdateBid)
            {
                if (invoice.BidAmount <= previousBidAmount)
                {
                    throw new RpcException(new RpcError(400, "BID_AMOUNT_TOO_LOW"));
                }
                effectiveBidAmount = invoice.BidAmount - previousBidAmount;
            }
            else
            {
                throw new RpcException(new RpcError(400, "BID_ALREADY_EXISTS"));
            }
        }

        // Check user's balance
        var balanceCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-userstarsbalancereadmodel");
        var balanceFilter = Builders<BsonDocument>.Filter.Eq("UserId", userId);
        var balanceDoc = await balanceCollection.Find(balanceFilter).FirstOrDefaultAsync();

        long currentBalance = 0;
        if (balanceDoc != null && balanceDoc.Contains("Balance"))
        {
            currentBalance = balanceDoc["Balance"].IsInt64 ? balanceDoc["Balance"].AsInt64 : balanceDoc["Balance"].AsInt32;
        }

        if (currentBalance < effectiveBidAmount)
        {
            RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();
        }

        // Deduct stars from user's balance
        var newBalance = currentBalance - effectiveBidAmount;
        if (balanceDoc != null)
        {
            var updateBalanceDoc = Builders<BsonDocument>.Update
                .Set("Balance", newBalance)
                .Set("LastUpdated", DateTime.UtcNow);
            await balanceCollection.UpdateOneAsync(balanceFilter, updateBalanceDoc);
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

        // Get recipient peer if specified (allow Self for self-gifting)
        long? recipientUserId = null;
        if (invoice.Peer != null)
        {
            var recipientPeer = peerHelper.GetPeer(invoice.Peer, input.UserId);
            if (recipientPeer.PeerType != PeerType.User && recipientPeer.PeerType != PeerType.Self)
            {
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            }
            recipientUserId = recipientPeer.PeerId;
            var recipientUser = await queryProcessor.ProcessAsync(new GetUserByIdQuery(recipientUserId.Value));
            if (recipientUser == null)
            {
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            }
        }

        // Create or update bid record
        if (existingBid != null && invoice.UpdateBid)
        {
            // Update existing bid
            var updateBid = Builders<BsonDocument>.Update
                .Set("BidAmount", invoice.BidAmount)
                .Set("BidDate", now)
                .Set("HideName", invoice.HideName)
                .Set("Message", invoice.Message?.Text)
                .Set("RecipientUserId", recipientUserId.HasValue ? (BsonValue)recipientUserId.Value : BsonNull.Value);
            await bidsCollection.UpdateOneAsync(existingBidFilter, updateBid);
        }
        else
        {
            // Create new bid
            var bidId = await idGenerator.NextIdAsync(IdType.AuctionBidId, userId);
            var newBidDoc = new BsonDocument
            {
                { "BidId", (long)bidId },
                { "UserId", userId },
                { "GiftId", invoice.GiftId },
                { "BidAmount", invoice.BidAmount },
                { "BidDate", now },
                { "HideName", invoice.HideName },
                { "Message", invoice.Message?.Text ?? (BsonValue)BsonNull.Value },
                { "RecipientUserId", recipientUserId.HasValue ? (BsonValue)recipientUserId.Value : BsonNull.Value },
                { "Returned", false },
                { "Won", false },
                { "AcquiredCount", 0 },
                { "BidPeerId", userId },
                { "BidPeerType", 1 } // User type
            };
            await bidsCollection.InsertOneAsync(newBidDoc);
        }

        // Update auction state - add to top bidders and update bid levels
        var newMinBid = await UpdateAuctionStateAsync(invoice.GiftId, userId, invoice.BidAmount, now);

        var auctionTitle = GetNullableString(giftDoc, "Title") ?? "Auction Gift";
        var auctionPeerId = recipientUserId ?? 0;
        var auctionPeerType = recipientUserId.HasValue ? (int)PeerType.User : (int)PeerType.Unknown;
        var auctionTransaction = StarsTransactionStore.CreateTransactionDocument(
            userId,
            -effectiveBidAmount,
            now,
            auctionPeerType,
            auctionPeerId,
            giftId: invoice.GiftId,
            title: auctionTitle,
            description: invoice.UpdateBid ? "Auction bid update" : "Auction bid",
            stargiftAuctionBid: true
        );
        await StarsTransactionStore.GetCollection(mongoDatabase).InsertOneAsync(auctionTransaction);

        // Get user info via converter service (handles privacy properly)
        var userList = await userConverterService.GetUserListAsync(input, [userId], true, true, input.Layer);
        var users = new TVector<IUser>();
        foreach (var user in userList)
        {
            users.Add(user);
        }

        // Create update for auction bid placed
        var userState = new TStarGiftAuctionUserState
        {
            BidAmount = invoice.BidAmount,
            BidDate = now,
            MinBidAmount = newMinBid,
            BidPeer = new TPeerUser { UserId = userId },
            Returned = false,
            AcquiredCount = 0
        };

        var updateUserState = new TUpdateStarGiftAuctionUserState
        {
            GiftId = invoice.GiftId,
            UserState = userState
        };
        var updateStarsBalance = new TUpdateStarsBalance
        {
            Balance = new TStarsAmount { Amount = newBalance }
        };

        return new MyTelegram.Schema.Payments.TPaymentResult
        {
            Updates = new TUpdates
            {
                Updates = new TVector<IUpdate>(updateUserState, updateStarsBalance),
                Users = users,
                Chats = [],
                Date = now,
                Seq = 0
            }
        };
    }

    private async Task<long> UpdateAuctionStateAsync(long giftId, long userId, long bidAmount, int bidDate)
    {
        var giftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stargiftreadmodel");
        var giftFilter = Builders<BsonDocument>.Filter.Eq("GiftId", giftId);
        var giftDoc = await giftsCollection.Find(giftFilter).FirstOrDefaultAsync();

        if (giftDoc == null) return 100;

        // Get current top bidders
        var topBidders = new List<long>();
        if (giftDoc.Contains("TopBidders") && !giftDoc["TopBidders"].IsBsonNull && giftDoc["TopBidders"].IsBsonArray)
        {
            foreach (var bidder in giftDoc["TopBidders"].AsBsonArray)
            {
                topBidders.Add(bidder.IsInt64 ? bidder.AsInt64 : bidder.AsInt32);
            }
        }

        // Add current user to top bidders if not already there
        if (!topBidders.Contains(userId))
        {
            topBidders.Add(userId);
        }

        // Get all active bids to sort top bidders
        var bidsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stargiftauctionbidreadmodel");
        var activeBidsFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("GiftId", giftId),
            Builders<BsonDocument>.Filter.Eq("Returned", false)
        );
        var activeBids = await bidsCollection.Find(activeBidsFilter)
            .Sort(Builders<BsonDocument>.Sort.Descending("BidAmount"))
            .Limit(100)
            .ToListAsync();

        // Sort top bidders by bid amount
        var sortedTopBidders = activeBids
            .Select(b => GetLong(b, "UserId"))
            .Distinct()
            .Take(10)
            .ToList();

        // Update bid levels based on current bids
        var bidLevels = new BsonArray();
        var giftsPerRound = GetNullableInt(giftDoc, "GiftsPerRound") ?? 10;

        for (var i = 0; i < Math.Min(activeBids.Count, giftsPerRound); i++)
        {
            var bid = activeBids[i];
            bidLevels.Add(new BsonDocument
            {
                { "Pos", i + 1 },
                { "Amount", GetLong(bid, "BidAmount") },
                { "Date", GetNullableInt(bid, "BidDate") ?? bidDate }
            });
        }

        // Calculate new minimum bid amount (highest losing bid + 1, or current min)
        var currentMinBid = GetNullableLong(giftDoc, "MinBidAmount") ?? 100;
        long newMinBid = currentMinBid;
        if (activeBids.Count >= giftsPerRound)
        {
            // Min bid should be higher than the lowest winning bid
            var lowestWinningBid = GetLong(activeBids[giftsPerRound - 1], "BidAmount");
            newMinBid = Math.Max(currentMinBid, lowestWinningBid + 1);
        }

        // Update gift document
        var update = Builders<BsonDocument>.Update
            .Set("TopBidders", new BsonArray(sortedTopBidders))
            .Set("BidLevels", bidLevels)
            .Set("MinBidAmount", newMinBid)
            .Set("AuctionVersion", (GetNullableInt(giftDoc, "AuctionVersion") ?? 0) + 1);

        await giftsCollection.UpdateOneAsync(giftFilter, update);

        return newMinBid;
    }

    private static long GetLong(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull) return 0;
        var value = doc[field];
        return value.IsInt64 ? value.AsInt64 : value.AsInt32;
    }

    private async Task<MyTelegram.Schema.Payments.IPaymentResult> HandleStarGiftUpgradePaymentAsync(
        IRequestInput input,
        long formId,
        TInputInvoiceStarGiftUpgrade invoice)
    {
        if (formId == 0)
        {
            RpcErrors.RpcErrors400.FormIdEmpty.ThrowRpcError();
        }

        var updates = await UpgradeStarGiftHandler.ProcessUpgradeAsync(
            mongoDatabase,
            peerHelper,
            idGenerator,
            userConverterService,
            objectMessageSender,
            input,
            invoice.Stargift,
            invoice.KeepOriginalDetails,
            chargeUpgrade: true);

        return new MyTelegram.Schema.Payments.TPaymentResult { Updates = updates };
    }

    /// <summary>
    /// Check if the sender is allowed to auto-save gifts to the recipient's profile
    /// based on the recipient's StarGiftsAutoSave privacy settings.
    /// </summary>
    /// <param name="senderId">The user sending the gift</param>
    /// <param name="recipientUserId">The user receiving the gift</param>
    /// <returns>True if gift should be auto-saved to profile, false otherwise</returns>
    private async Task<bool> CheckAutoSaveGiftPrivacyAsync(long senderId, long recipientUserId)
    {
        // Default to true (auto-save enabled) - gifts are saved to profile by default
        var autoSave = true;

        // Check recipient's privacy settings for StarGiftsAutoSave
        await privacyAppService.ApplyPrivacyAsync(
            senderId,
            recipientUserId,
            _ =>
            {
                // Privacy check failed - sender is not allowed to auto-save gifts
                autoSave = false;
            },
            PrivacyType.StarGiftsAutoSave);

        return autoSave;
    }
}
