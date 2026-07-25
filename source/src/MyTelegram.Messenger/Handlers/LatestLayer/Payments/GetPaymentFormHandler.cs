using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Converters.ConverterServices;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Get a payment form
/// Possible errors
/// Code Type Description
/// 406 API_GIFT_RESTRICTED_UPDATE_APP Please update the app to access the gift API.
/// 400 BOOST_PEER_INVALID The specified <code>boost_peer</code> is invalid.
/// 403 BOT_ACCESS_FORBIDDEN The specified method <em>can</em> be used over a <a href="https://corefork.telegram.org/api/bots/connected-business-bots">business connection</a> for some operations, but the specified query attempted an operation that is not allowed over a business connection.
/// 400 BOT_INVOICE_INVALID The specified invoice is invalid.
/// 400 BUSINESS_CONNECTION_INVALID The <code>connection_id</code> passed to the wrapping <a href="https://corefork.telegram.org/api/business">invokeWithBusinessConnection</a> call is invalid.
/// 400 GIFT_MONTHS_INVALID The value passed in invoice.inputInvoicePremiumGiftStars.months is invalid.
/// 400 INVOICE_INVALID The specified invoice is invalid.
/// 400 MESSAGE_ID_INVALID The provided message id is invalid.
/// 400 MONTH_INVALID The number of months specified in inputInvoicePremiumGiftStars.months is invalid.
/// 400 NO_PAYMENT_NEEDED The upgrade/transfer of the specified gift was already paid for or is free.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 SLUG_INVALID The specified invoice slug is invalid.
/// 400 STARGIFT_ALREADY_CONVERTED The specified star gift was already converted to Stars.
/// 400 STARGIFT_ALREADY_REFUNDED The specified star gift was already refunded.
/// 400 STARGIFT_ALREADY_UPGRADED The specified gift was already upgraded to a collectible gift.
/// 406 STARGIFT_EXPORT_IN_PROGRESS A gift export is in progress, a detailed and localized description for the error will be emitted via an <a href="https://corefork.telegram.org/api/errors#406-not-acceptable">updateServiceNotification as specified here »</a>.
/// 400 STARGIFT_INVALID The passed gift is invalid.
/// 400 STARGIFT_NOT_FOUND The specified gift was not found.
/// 400 STARGIFT_OWNER_INVALID You cannot transfer or sell a gift owned by another user.
/// 400 STARGIFT_PEER_INVALID The specified inputSavedStarGiftChat.peer is invalid.
/// 400 STARGIFT_RESELL_CURRENCY_NOT_ALLOWED You can't buy the gift using the specified currency (i.e. trying to pay in Stars for TON gifts).
/// 400 STARGIFT_SLUG_INVALID The specified gift slug is invalid.
/// 400 STARGIFT_TRANSFER_TOO_EARLY_%d You cannot transfer this gift yet, wait %d seconds.
/// 400 STARGIFT_UPGRADE_UNAVAILABLE A received gift can only be upgraded to a collectible gift if the <a href="https://corefork.telegram.org/constructor/messageActionStarGift">messageActionStarGift</a>/<a href="https://corefork.telegram.org/constructor/savedStarGift">savedStarGift</a>.<code>can_upgrade</code> flag is set.
/// 406 STARS_FORM_AMOUNT_MISMATCH The form amount has changed, please fetch the new form using <a href="https://corefork.telegram.org/method/payments.getPaymentForm">payments.getPaymentForm</a> and restart the process.
/// 400 TO_ID_INVALID The specified <code>to_id</code> of the passed inputInvoiceStarGiftResale or inputInvoiceStarGiftTransfer is invalid.
/// 400 UNTIL_DATE_INVALID Invalid until date provided.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.getPaymentForm"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✔]
/// </remarks>
internal sealed class GetPaymentFormHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IChannelAppService channelAppService,
    ILayeredService<IDocumentConverter> documentConverterService,
    IUserConverterService userConverterService,
    IQueryProcessor queryProcessor,
    IIdGenerator idGenerator) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetPaymentForm, MyTelegram.Schema.Payments.IPaymentForm>
{
    protected override async Task<MyTelegram.Schema.Payments.IPaymentForm> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetPaymentForm obj)
    {
        if (obj.Invoice is TInputInvoiceStars starsInvoice)
        {
            return await HandleStarsInvoiceAsync(input, starsInvoice);
        }

        if (obj.Invoice is TInputInvoiceStarGift starGiftInvoice)
        {
            return await HandleStarGiftInvoiceAsync(input, starGiftInvoice);
        }

        if (obj.Invoice is TInputInvoiceStarGiftResale resaleInvoice)
        {
            return await HandleStarGiftResaleInvoiceAsync(input, resaleInvoice);
        }

        if (obj.Invoice is TInputInvoiceStarGiftAuctionBid auctionBidInvoice)
        {
            return await HandleAuctionBidInvoiceAsync(input, auctionBidInvoice);
        }

        if (obj.Invoice is TInputInvoiceStarGiftUpgrade upgradeInvoice)
        {
            return await HandleStarGiftUpgradeInvoiceAsync(input, upgradeInvoice);
        }

        RpcErrors.RpcErrors400.InvoiceInvalid.ThrowRpcError();
        throw new RpcException(new RpcError(400, "INVOICE_INVALID"));
    }

    private async Task<MyTelegram.Schema.Payments.IPaymentForm> HandleStarsInvoiceAsync(
        IRequestInput input,
        TInputInvoiceStars invoice)
    {
        var formId = DateTime.UtcNow.Ticks ^ Random.Shared.NextInt64();
        var users = new TVector<IUser>();

        string title;
        string description;
        string label;
        string currency;
        long amount;

        switch (invoice.Purpose)
        {
            case TInputStorePaymentStarsTopup topupPurpose:
                ValidateStoreStarsPayment(topupPurpose.Stars, topupPurpose.Amount, topupPurpose.Currency);
                title = "Telegram Stars";
                description = $"Top up {topupPurpose.Stars} Stars";
                label = "Stars Topup";
                currency = topupPurpose.Currency;
                amount = topupPurpose.Amount;
                break;

            case TInputStorePaymentStarsGift giftPurpose:
                ValidateStoreStarsPayment(giftPurpose.Stars, giftPurpose.Amount, giftPurpose.Currency);
                var peer = peerHelper.GetPeer(giftPurpose.UserId, input.UserId);
                if (peer.PeerType != PeerType.User && peer.PeerType != PeerType.Self)
                {
                    RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
                }

                var recipientUser = await queryProcessor.ProcessAsync(new GetUserByIdQuery(peer.PeerId));
                if (recipientUser == null)
                {
                    RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
                }

                title = "Telegram Stars Gift";
                description = $"Gift {giftPurpose.Stars} Stars to {recipientUser.FirstName}";
                label = "Stars Gift";
                currency = giftPurpose.Currency;
                amount = giftPurpose.Amount;

                var userList = await userConverterService.GetUserListAsync(input, [peer.PeerId], true, true, input.Layer);
                foreach (var user in userList)
                {
                    users.Add(user);
                }
                break;

            default:
                RpcErrors.RpcErrors400.PurposeInvalid.ThrowRpcError();
                throw new RpcException(new RpcError(400, "PURPOSE_INVALID"));
        }

        return new MyTelegram.Schema.Payments.TPaymentForm
        {
            CanSaveCredentials = true,
            FormId = formId,
            BotId = input.UserId,
            Title = title,
            Description = description,
            Invoice = new TInvoice
            {
                Test = true,
                Currency = currency,
                Prices = new TVector<ILabeledPrice>
                {
                    new TLabeledPrice
                    {
                        Label = label,
                        Amount = amount
                    }
                }
            },
            ProviderId = 1,
            Url = $"https://checkout.stripe.com/pay/cs_test_local_{formId}",
            NativeProvider = "stripe",
            NativeParams = new TDataJSON
            {
                Data = "{\"need_country\":false,\"need_zip\":false,\"need_cardholder_name\":true}"
            },
            Users = users
        };
    }

    private async Task<MyTelegram.Schema.Payments.IPaymentForm> HandleStarGiftResaleInvoiceAsync(
        IRequestInput input,
        TInputInvoiceStarGiftResale invoice)
    {
        if (invoice.Ton)
        {
            RpcErrors.RpcErrors400.StargiftResellCurrencyNotAllowed.ThrowRpcError();
        }

        var recipientPeer = peerHelper.GetPeer(invoice.ToId, input.UserId);
        if (recipientPeer.PeerType != PeerType.User &&
            recipientPeer.PeerType != PeerType.Self &&
            recipientPeer.PeerType != PeerType.Channel)
        {
            RpcErrors.RpcErrors400.ToIdInvalid.ThrowRpcError();
        }

        IUserReadModel? recipientUser = null;
        IChannelReadModel? recipientChannel = null;
        if (recipientPeer.PeerType == PeerType.Channel)
        {
            recipientChannel = await channelAppService.GetAsync(recipientPeer.PeerId);
            if (recipientChannel == null)
            {
                RpcErrors.RpcErrors400.ToIdInvalid.ThrowRpcError();
            }
        }
        else
        {
            recipientUser = await queryProcessor.ProcessAsync(new GetUserByIdQuery(recipientPeer.PeerId));
            if (recipientUser == null)
            {
                RpcErrors.RpcErrors400.ToIdInvalid.ThrowRpcError();
            }
        }

        var savedGiftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-savedstargiftreadmodel");
        var savedGiftFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("Slug", invoice.Slug),
            Builders<BsonDocument>.Filter.Eq("Upgraded", true),
            Builders<BsonDocument>.Filter.Ne("Converted", true),
            Builders<BsonDocument>.Filter.Ne("Refunded", true)
        );
        var savedGiftDoc = await savedGiftsCollection.Find(savedGiftFilter).FirstOrDefaultAsync();
        if (savedGiftDoc == null)
        {
            RpcErrors.RpcErrors400.StargiftSlugInvalid.ThrowRpcError();
        }

        if (!StarGiftResaleHelper.TryGetResaleStarsAmount(savedGiftDoc!, out var resaleAmount, out _))
        {
            RpcErrors.RpcErrors400.StargiftNotFound.ThrowRpcError();
        }

        var sellerId = GetLong(savedGiftDoc, "OwnerUserId");
        if (sellerId == input.UserId)
        {
            RpcErrors.RpcErrors400.StargiftOwnerInvalid.ThrowRpcError();
        }

        var giftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stargiftreadmodel");
        var giftId = GetLong(savedGiftDoc, "GiftId");
        var giftDoc = await giftsCollection.Find(Builders<BsonDocument>.Filter.Eq("GiftId", giftId)).FirstOrDefaultAsync();

        var giftTitle = GetNullableString(giftDoc ?? savedGiftDoc, "Title") ?? "Collectible Gift";
        var recipientTitle = recipientPeer.PeerType == PeerType.Channel
            ? recipientChannel!.Title
            : recipientUser!.FirstName;

        var users = new TVector<IUser>();
        if (recipientPeer.PeerType != PeerType.Channel)
        {
            var userList = await userConverterService.GetUserListAsync(input, [recipientPeer.PeerId], true, true, input.Layer);
            foreach (var user in userList)
            {
                users.Add(user);
            }
        }

        var formId = DateTime.UtcNow.Ticks ^ Random.Shared.NextInt64();
        return new MyTelegram.Schema.Payments.TPaymentFormStarGift
        {
            FormId = formId,
            Invoice = new TInvoice
            {
                Currency = "XTR",
                Prices = new TVector<ILabeledPrice>
                {
                    new TLabeledPrice
                    {
                        Label = "Gift Resale",
                        Amount = resaleAmount
                    }
                }
            }
        };
    }

    private async Task<MyTelegram.Schema.Payments.IPaymentForm> HandleStarGiftUpgradeInvoiceAsync(IRequestInput input, TInputInvoiceStarGiftUpgrade invoice)
    {
        var userId = input.UserId;
        var savedGiftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-savedstargiftreadmodel");
        var giftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stargiftreadmodel");

        // Find the saved gift
        BsonDocument? savedGiftDoc = null;

        switch (invoice.Stargift)
        {
            case TInputSavedStarGiftUser userGift:
                var userFilter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("OwnerUserId", userId),
                    Builders<BsonDocument>.Filter.Eq("MsgId", userGift.MsgId)
                );
                savedGiftDoc = await savedGiftsCollection.Find(userFilter).FirstOrDefaultAsync();
                
                if (savedGiftDoc == null)
                {
                    userFilter = Builders<BsonDocument>.Filter.And(
                        Builders<BsonDocument>.Filter.Eq("OwnerUserId", userId),
                        Builders<BsonDocument>.Filter.Eq("SavedId", (long)userGift.MsgId)
                    );
                    savedGiftDoc = await savedGiftsCollection.Find(userFilter).FirstOrDefaultAsync();
                }
                break;

            case TInputSavedStarGiftChat chatGift:
                var chatPeer = peerHelper.GetPeer(chatGift.Peer, userId);
                if (chatGift.SavedId == 0)
                    RpcErrors.RpcErrors400.SavedIdEmpty.ThrowRpcError();
                var chatFilter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("OwnerUserId", chatPeer.PeerId),
                    Builders<BsonDocument>.Filter.Eq("SavedId", chatGift.SavedId)
                );
                savedGiftDoc = await savedGiftsCollection.Find(chatFilter).FirstOrDefaultAsync();
                break;

            case TInputSavedStarGiftSlug slugGift:
                var slugFilter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("OwnerUserId", userId),
                    Builders<BsonDocument>.Filter.Eq("Slug", slugGift.Slug)
                );
                savedGiftDoc = await savedGiftsCollection.Find(slugFilter).FirstOrDefaultAsync();
                break;

            default:
                RpcErrors.RpcErrors400.StargiftPeerInvalid.ThrowRpcError();
                return null!;
        }

        if (savedGiftDoc == null)
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();

        var ownerUserId = GetLong(savedGiftDoc!, "OwnerUserId");
        if (ownerUserId != userId)
            RpcErrors.RpcErrors400.StargiftOwnerInvalid.ThrowRpcError();

        if (savedGiftDoc!.GetValue("Converted", false).AsBoolean)
            RpcErrors.RpcErrors400.StargiftAlreadyConverted.ThrowRpcError();

        if (savedGiftDoc.GetValue("Upgraded", false).AsBoolean)
            RpcErrors.RpcErrors400.StargiftAlreadyUpgraded.ThrowRpcError();

        var giftId = GetLong(savedGiftDoc, "GiftId");
        var giftDoc = await giftsCollection.Find(
            Builders<BsonDocument>.Filter.Eq("GiftId", giftId)
        ).FirstOrDefaultAsync();

        if (giftDoc == null)
            RpcErrors.RpcErrors400.StargiftNotFound.ThrowRpcError();

        await StarGiftUpgradeStateHelper.SyncCanUpgradeAsync(savedGiftsCollection, savedGiftDoc, giftDoc);

        if (!savedGiftDoc.GetValue("CanUpgrade", false).AsBoolean)
            RpcErrors.RpcErrors400.StargiftUpgradeUnavailable.ThrowRpcError();

        if (StarGiftUpgradeStateHelper.IsUpgradeAlreadyPrepaid(savedGiftDoc))
            RpcErrors.RpcErrors400.NoPaymentNeeded.ThrowRpcError();

        // Craft upgrades burn multiple gifts and are executed directly via payments.upgradeStarGift.
        var craftRequiredCount = GetNullableInt(giftDoc, "CraftRequiredCount") ?? 1;
        if (craftRequiredCount > 1)
            RpcErrors.RpcErrors400.NoPaymentNeeded.ThrowRpcError();

        // Get upgrade cost - first from saved gift, then from gift definition
        var upgradeStars = GetNullableLong(savedGiftDoc, "UpgradeStars");
        if (!upgradeStars.HasValue)
        {
            upgradeStars = GetNullableLong(giftDoc, "UpgradeStars");
        }

        // If UpgradeStars is null, upgrade is not available
        if (!upgradeStars.HasValue)
            RpcErrors.RpcErrors400.StargiftUpgradeUnavailable.ThrowRpcError();

        // If UpgradeStars is 0, upgrade is free - no payment needed
        if (upgradeStars!.Value == 0)
            RpcErrors.RpcErrors400.NoPaymentNeeded.ThrowRpcError();

        // Generate form ID
        var formId = DateTime.UtcNow.Ticks ^ Random.Shared.NextInt64();

        // Get gift info for title
        var giftTitle = giftDoc.Contains("Title") && !giftDoc["Title"].IsBsonNull
            ? giftDoc["Title"].AsString
            : "Star Gift";

        // Get user info
        var userList = await userConverterService.GetUserListAsync(input, [userId], true, true, input.Layer);
        var users = new TVector<IUser>();
        foreach (var user in userList)
        {
            users.Add(user);
        }

        return new MyTelegram.Schema.Payments.TPaymentFormStarGift
        {
            FormId = formId,
            Invoice = new TInvoice
            {
                Currency = "XTR",
                Prices = new TVector<ILabeledPrice>
                {
                    new TLabeledPrice
                    {
                        Label = "Gift Upgrade",
                        Amount = upgradeStars.Value
                    }
                }
            }
        };
    }

    private async Task<MyTelegram.Schema.Payments.IPaymentForm> HandleStarGiftInvoiceAsync(IRequestInput input, TInputInvoiceStarGift invoice)
    {
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const int IncludeUpgradeFlag = 1 << 2;
        var includeUpgrade = (invoice.Flags & IncludeUpgradeFlag) != 0;

        // Get the gift from database
        var giftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stargiftreadmodel");
        var giftFilter = Builders<BsonDocument>.Filter.Eq("GiftId", invoice.GiftId);
        var giftDoc = await giftsCollection.Find(giftFilter).FirstOrDefaultAsync();

        if (giftDoc == null)
        {
            RpcErrors.RpcErrors400.StargiftNotFound.ThrowRpcError();
        }

        // Check if gift is sold out
        if (giftDoc.GetValue("SoldOut", false).AsBoolean)
        {
            RpcErrors.RpcErrors400.StargiftUsageLimited.ThrowRpcError();
        }

        if (giftDoc.GetValue("Auction", false).AsBoolean)
        {
            // Desktop may still send inputInvoiceStarGift for auction items.
            // Convert it to a minimum bid payment form for backward compatibility.
            if (input.DeviceType == DeviceType.Desktop)
            {
                const int HideNameFlag = 1 << 0;
                const int MessageFlag = 1 << 1;
                var minBidAmount = GetNullableLong(giftDoc, "MinBidAmount") ?? 100;
                var auctionInvoice = new TInputInvoiceStarGiftAuctionBid
                {
                    GiftId = invoice.GiftId,
                    BidAmount = minBidAmount,
                    UpdateBid = false,
                    HideName = (invoice.Flags & HideNameFlag) != 0,
                    Message = (invoice.Flags & MessageFlag) != 0 ? invoice.Message : null,
                    Peer = invoice.Peer
                };
                return await HandleAuctionBidInvoiceAsync(input, auctionInvoice);
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
            var senderUser = await queryProcessor.ProcessAsync(new GetUserByIdQuery(input.UserId));
            if (senderUser == null)
            {
                RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
            }
            if (!senderUser.Premium)
            {
                RpcErrors.RpcErrors400.PremiumAccountRequired.ThrowRpcError();
            }
        }

        // Get gift price
        var stars = giftDoc["Stars"].IsInt64 ? giftDoc["Stars"].AsInt64 : giftDoc["Stars"].AsInt32;
        
        // Add upgrade cost if include_upgrade is set
        long totalPrice = stars;
        long? upgradeStars = null;
        
        if (includeUpgrade)
        {
            upgradeStars = GetNullableLong(giftDoc, "UpgradeStars");
            if (!upgradeStars.HasValue)
            {
                RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
            }
            if (upgradeStars.HasValue && upgradeStars.Value > 0)
            {
                totalPrice += upgradeStars.Value;
            }
            else
            {
                // Gift doesn't support upgrade, ignore include_upgrade flag
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

        IUserReadModel? recipientUser = null;
        IChannelReadModel? recipientChannel = null;
        if (recipientPeer.PeerType == PeerType.Channel)
        {
            recipientChannel = await channelAppService.GetAsync(recipientPeer.PeerId);
            if (recipientChannel == null)
            {
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            }
        }
        else
        {
            recipientUser = await queryProcessor.ProcessAsync(new GetUserByIdQuery(recipientPeer.PeerId));
            if (recipientUser == null)
            {
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            }
        }

        // Generate form ID (use timestamp + random for uniqueness)
        var formId = DateTime.UtcNow.Ticks ^ Random.Shared.NextInt64();

        // Get sticker for the gift
        var stickerId = giftDoc["StickerId"].IsInt64 ? giftDoc["StickerId"].AsInt64 : giftDoc["StickerId"].AsInt32;
        var documentsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");
        var docFilter = Builders<BsonDocument>.Filter.Eq("DocumentId", stickerId);
        var stickerDoc = await documentsCollection.Find(docFilter).FirstOrDefaultAsync();

        // Build the gift title
        var giftTitle = giftDoc.Contains("Title") && !giftDoc["Title"].IsBsonNull
            ? giftDoc["Title"].AsString
            : "Star Gift";

        var users = new TVector<IUser>();
        if (recipientUser != null)
        {
            var userList = await userConverterService.GetUserListAsync(input, [recipientPeer.PeerId], true, true, input.Layer);
            foreach (var user in userList)
            {
                users.Add(user);
            }
        }

        // Build price labels
        var prices = new TVector<ILabeledPrice>
        {
            new TLabeledPrice
            {
                Label = "Star Gift",
                Amount = stars
            }
        };
        
        if (includeUpgrade && upgradeStars.HasValue)
        {
            prices.Add(new TLabeledPrice
            {
                Label = "Gift Upgrade",
                Amount = upgradeStars.Value
            });
        }

        var recipientTitle = recipientPeer.PeerType == PeerType.Channel
            ? recipientChannel!.Title
            : recipientUser!.FirstName;

        // Create payment form for stars
        return new MyTelegram.Schema.Payments.TPaymentFormStarGift
        {
            FormId = formId,
            Invoice = new TInvoice
            {
                Currency = "XTR",
                Prices = prices
            }
        };
    }

    private async Task<MyTelegram.Schema.Payments.IPaymentForm> HandleAuctionBidInvoiceAsync(IRequestInput input, TInputInvoiceStarGiftAuctionBid invoice)
    {
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
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

        // Check if user already has a bid and if this is an update
        var bidsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stargiftauctionbidreadmodel");
        var existingBidFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("GiftId", invoice.GiftId),
            Builders<BsonDocument>.Filter.Eq("Returned", false)
        );
        var existingBid = await bidsCollection.Find(existingBidFilter).FirstOrDefaultAsync();

        long effectiveBidAmount = invoice.BidAmount;
        if (existingBid != null && invoice.UpdateBid)
        {
            // For update bid, user only pays the difference
            var currentBidAmount = GetLong(existingBid, "BidAmount");
            if (invoice.BidAmount <= currentBidAmount)
            {
                throw new RpcException(new RpcError(400, "BID_AMOUNT_TOO_LOW"));
            }
            effectiveBidAmount = invoice.BidAmount - currentBidAmount;
        }
        else if (existingBid != null && !invoice.UpdateBid)
        {
            throw new RpcException(new RpcError(400, "BID_ALREADY_EXISTS"));
        }

        // Get recipient peer if specified (for gifting the won item, allow Self for self-gifting)
        long? recipientUserId = null;
        IUserReadModel? recipientUser = null;
        if (invoice.Peer != null)
        {
            var recipientPeer = peerHelper.GetPeer(invoice.Peer, input.UserId);
            if (recipientPeer.PeerType != PeerType.User && recipientPeer.PeerType != PeerType.Self)
            {
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            }
            recipientUserId = recipientPeer.PeerId;
            recipientUser = await queryProcessor.ProcessAsync(new GetUserByIdQuery(recipientUserId.Value));
            if (recipientUser == null)
            {
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            }
        }

        // Generate form ID
        var formId = DateTime.UtcNow.Ticks ^ Random.Shared.NextInt64();

        // Get sticker for the gift
        var stickerId = GetLong(giftDoc, "StickerId");
        var documentsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");
        var docFilter = Builders<BsonDocument>.Filter.Eq("DocumentId", stickerId);
        var stickerDoc = await documentsCollection.Find(docFilter).FirstOrDefaultAsync();

        // Build the gift title
        var giftTitle = giftDoc.Contains("Title") && !giftDoc["Title"].IsBsonNull
            ? giftDoc["Title"].AsString
            : "Auction Gift";

        var description = invoice.UpdateBid
            ? $"Update bid on {giftTitle}"
            : $"Place bid on {giftTitle}";

        if (recipientUser != null)
        {
            description += $" for {recipientUser.FirstName}";
        }

        // Build users list via converter service (handles privacy properly)
        var users = new TVector<IUser>();
        if (recipientUser != null)
        {
            var userList = await userConverterService.GetUserListAsync(input, [recipientUserId!.Value], true, true, input.Layer);
            foreach (var user in userList)
            {
                users.Add(user);
            }
        }

        // Create payment form for stars
        return new MyTelegram.Schema.Payments.TPaymentFormStarGift
        {
            FormId = formId,
            Invoice = new TInvoice
            {
                Currency = "XTR",
                Prices = new TVector<ILabeledPrice>
                {
                    new TLabeledPrice
                    {
                        Label = invoice.UpdateBid ? "Bid Increase" : "Auction Bid",
                        Amount = effectiveBidAmount
                    }
                }
            }
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

    private static string? GetNullableString(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull) return null;
        return doc[field].AsString;
    }

    private static void ValidateStoreStarsPayment(long stars, long amount, string? currency)
    {
        if (stars <= 0 || amount <= 0 || string.IsNullOrWhiteSpace(currency))
        {
            RpcErrors.RpcErrors400.PurposeInvalid.ThrowRpcError();
        }
    }
}
