
using EventFlow.Exceptions;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Text.RegularExpressions;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

/// <summary>
/// <para><c>See <a href="" /> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ] [Bot ] [Anonymous ]
/// </remarks>
internal sealed class SendStarGiftOfferHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IIdGenerator idGenerator,
    IQueryProcessor queryProcessor,
    IUserConverterService userConverterService,
    ICommandBus commandBus,
    IObjectMessageSender objectMessageSender) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestSendStarGiftOffer, MyTelegram.Schema.IUpdates>, IObjectHandler
{
    private const string SavedGiftsCollectionName = "eventflow-savedstargiftreadmodel";
    private const string GiftsCollectionName = "eventflow-stargiftreadmodel";
    private const string OffersCollectionName = "eventflow-stargiftofferreadmodel";

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestSendStarGiftOffer obj)
    {
        var senderId = input.UserId;
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var normalizedSlug = NormalizeSlug(obj.Slug);

        if (obj.Duration <= 0)
        {
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
        }
        if (string.IsNullOrWhiteSpace(normalizedSlug))
        {
            RpcErrors.RpcErrors400.StargiftSlugInvalid.ThrowRpcError();
        }

        var starsPrice = obj.Price as TStarsAmount;
        if (starsPrice == null || starsPrice.Amount <= 0 || starsPrice.Nanos != 0)
        {
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
        }

        var recipientPeer = peerHelper.GetPeer(obj.Peer, senderId);
        if (recipientPeer.PeerType != PeerType.User && recipientPeer.PeerType != PeerType.Self)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var recipientId = recipientPeer.PeerId;
        if (recipientId <= 0 || recipientId == senderId)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var recipientUser = await queryProcessor.ProcessAsync(new GetUserByIdQuery(recipientId));
        if (recipientUser == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var savedGiftsCollection = mongoDatabase.GetCollection<BsonDocument>(SavedGiftsCollectionName);
        var giftsCollection = mongoDatabase.GetCollection<BsonDocument>(GiftsCollectionName);
        var offersCollection = mongoDatabase.GetCollection<BsonDocument>(OffersCollectionName);

        var activeGiftFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Ne("Converted", true),
            Builders<BsonDocument>.Filter.Ne("Refunded", true)
        );
        var slugFilter = BuildSlugFilter(normalizedSlug);
        // sendStarGiftOffer is sent to the gift owner (peer).
        var savedGiftFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("OwnerUserId", recipientId),
            slugFilter,
            activeGiftFilter
        );

        var savedGiftDoc = await savedGiftsCollection
            .Find(savedGiftFilter)
            .Sort(Builders<BsonDocument>.Sort.Descending("SavedId"))
            .FirstOrDefaultAsync();
        if (savedGiftDoc == null)
        {
            var existingSlugDoc = await savedGiftsCollection
                .Find(Builders<BsonDocument>.Filter.And(slugFilter, activeGiftFilter))
                .Project(Builders<BsonDocument>.Projection.Include("OwnerUserId"))
                .FirstOrDefaultAsync();
            if (existingSlugDoc != null)
            {
                RpcErrors.RpcErrors400.StargiftOwnerInvalid.ThrowRpcError();
            }

            RpcErrors.RpcErrors400.StargiftSlugInvalid.ThrowRpcError();
        }
        if (!savedGiftDoc!.GetValue("Upgraded", false).AsBoolean)
        {
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
        }

        var giftId = GetLong(savedGiftDoc!, "GiftId");
        var giftDoc = await giftsCollection.Find(Builders<BsonDocument>.Filter.Eq("GiftId", giftId)).FirstOrDefaultAsync();
        if (giftDoc == null)
        {
            RpcErrors.RpcErrors400.StargiftNotFound.ThrowRpcError();
        }

        var minOfferFromGift = GetNullableLong(giftDoc!, "ResellMinStars") ?? 0;
        var minOfferFromSavedGift = GetNullableLong(savedGiftDoc, "OfferMinStars") ?? 0;
        var minOffer = Math.Max(minOfferFromGift, minOfferFromSavedGift);
        if (starsPrice!.Amount < minOffer)
        {
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
        }

        var expiresAt = now + obj.Duration;
        var senderMessageId = await idGenerator.NextIdAsync(IdType.MessageId, senderId);
        var senderPts = await idGenerator.NextIdAsync(IdType.Pts, senderId);
        var randomId = obj.RandomId != 0 ? obj.RandomId : Random.Shared.NextInt64();

        var gift = BuildOfferGift(savedGiftDoc, giftDoc!);
        var actionPrice = new TStarsAmount
        {
            Amount = starsPrice.Amount,
            Nanos = starsPrice.Nanos
        };
        var messageAction = new TMessageActionStarGiftPurchaseOffer
        {
            Accepted = false,
            Declined = false,
            Gift = gift,
            Price = actionPrice,
            ExpiresAt = expiresAt
        };

        var offerDoc = new BsonDocument
        {
            { "SenderUserId", senderId },
            { "RecipientUserId", recipientId },
            { "SenderMessageId", senderMessageId },
            { "RecipientMessageId", BsonNull.Value },
            { "GiftSavedId", GetLong(savedGiftDoc, "SavedId") },
            { "GiftId", giftId },
            { "Slug", GetNullableString(savedGiftDoc, "Slug") ?? normalizedSlug },
            { "PriceAmount", actionPrice.Amount },
            { "PriceNanos", actionPrice.Nanos },
            { "Duration", obj.Duration },
            { "ExpiresAt", expiresAt },
            { "AllowPaidStars", obj.AllowPaidStars.HasValue ? obj.AllowPaidStars.Value : BsonNull.Value },
            { "Status", "Pending" },
            { "CreatedAt", now },
            { "ResolvedAt", BsonNull.Value },
            { "ResolvedByUserId", BsonNull.Value },
            { "BuyerUserId", BsonNull.Value }
        };
        await offersCollection.InsertOneAsync(offerDoc);

        var senderMessage = new TMessageService
        {
            Id = senderMessageId,
            FromId = new TPeerUser { UserId = senderId },
            PeerId = new TPeerUser { UserId = recipientId },
            Date = now,
            Out = true,
            Action = messageAction
        };

        var senderUpdates = new TVector<IUpdate>
        {
            new TUpdateMessageID { Id = senderMessageId, RandomId = randomId },
            new TUpdateNewMessage
            {
                Message = senderMessage,
                Pts = senderPts,
                PtsCount = 1
            }
        };

        var messageItem = new MessageItem(
            senderId.ToUserPeer(),
            recipientId.ToUserPeer(),
            senderId.ToUserPeer(),
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
            Pts: senderPts
        );

        var command = new StartSendMessageCommand(
            TempId.New,
            input.ToRequestInfo(),
            [new SendMessageItem(messageItem)]
        );
        var deliveredByCommandBus = true;
        try
        {
            await commandBus.PublishAsync(command);
        }
        catch (NoCommandHandlersException)
        {
            deliveredByCommandBus = false;
        }

        var userList = await userConverterService.GetUserListAsync(input, [senderId, recipientId], true, true, input.Layer);
        var users = new TVector<IUser>();
        foreach (var user in userList)
        {
            users.Add(user);
        }

        if (!deliveredByCommandBus)
        {
            var recipientMessageId = await idGenerator.NextIdAsync(IdType.MessageId, recipientId);
            var recipientPts = await idGenerator.NextIdAsync(IdType.Pts, recipientId);
            await offersCollection.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", offerDoc["_id"]),
                Builders<BsonDocument>.Update.Set("RecipientMessageId", recipientMessageId));

            var recipientMessage = new TMessageService
            {
                Id = recipientMessageId,
                FromId = new TPeerUser { UserId = senderId },
                PeerId = new TPeerUser { UserId = senderId },
                Date = now,
                Out = false,
                Action = messageAction
            };

            var senderOtherDevicesUpdates = new TUpdates
            {
                Updates =
                [
                    new TUpdateNewMessage
                    {
                        Message = senderMessage,
                        Pts = senderPts,
                        PtsCount = 1
                    }
                ],
                Users = users,
                Chats = [],
                Date = now,
                Seq = 0
            };
            await objectMessageSender.PushMessageToPeerAsync(
                new Peer(PeerType.User, senderId),
                senderOtherDevicesUpdates,
                excludeAuthKeyId: input.PermAuthKeyId,
                pts: senderPts);

            var recipientUpdates = new TUpdates
            {
                Updates =
                [
                    new TUpdateNewMessage
                    {
                        Message = recipientMessage,
                        Pts = recipientPts,
                        PtsCount = 1
                    }
                ],
                Users = users,
                Chats = [],
                Date = now,
                Seq = 0
            };
            await objectMessageSender.PushMessageToPeerAsync(
                new Peer(PeerType.User, recipientId),
                recipientUpdates,
                excludeUserId: senderId,
                pts: recipientPts);
        }

        return new TUpdates
        {
            Updates = senderUpdates,
            Users = users,
            Chats = [],
            Date = now,
            Seq = 0
        };
    }

    private static TStarGiftUnique BuildOfferGift(BsonDocument savedGiftDoc, BsonDocument giftDoc)
    {
        var minOffer = GetNullableLong(savedGiftDoc, "OfferMinStars");
        if (!minOffer.HasValue)
        {
            minOffer = GetNullableLong(giftDoc, "ResellMinStars");
        }

        return new TStarGiftUnique
        {
            Id = GetLong(savedGiftDoc, "SavedId"),
            GiftId = GetLong(savedGiftDoc, "GiftId"),
            Title = GetNullableString(giftDoc, "Title") ?? "Collectible Gift",
            Slug = GetNullableString(savedGiftDoc, "Slug") ?? string.Empty,
            Num = GetNullableInt(savedGiftDoc, "GiftNum") ?? 1,
            OwnerId = new TPeerUser { UserId = GetLong(savedGiftDoc, "OwnerUserId") },
            Attributes = [],
            AvailabilityIssued = GetNullableInt(giftDoc, "AvailabilityTotal") ?? 0,
            AvailabilityTotal = GetNullableInt(giftDoc, "AvailabilityTotal") ?? 0,
            ResellAmount = StarGiftResaleHelper.BuildResellAmount(savedGiftDoc),
            OfferMinStars = minOffer.HasValue && minOffer.Value <= int.MaxValue ? (int)minOffer.Value : null
        };
    }

    private static long GetLong(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull)
        {
            return 0;
        }

        var value = doc[field];
        return value.IsInt64 ? value.AsInt64 : value.AsInt32;
    }

    private static int? GetNullableInt(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull)
        {
            return null;
        }

        return doc[field].IsInt32 ? doc[field].AsInt32 : (int)doc[field].AsInt64;
    }

    private static long? GetNullableLong(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull)
        {
            return null;
        }

        return doc[field].IsInt64 ? doc[field].AsInt64 : doc[field].AsInt32;
    }

    private static string? GetNullableString(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull)
        {
            return null;
        }

        return doc[field].AsString;
    }

    private static FilterDefinition<BsonDocument> BuildSlugFilter(string slug)
    {
        var exact = Builders<BsonDocument>.Filter.Eq("Slug", slug);
        var escaped = Regex.Escape(slug);
        var trimmedCaseInsensitive = Builders<BsonDocument>.Filter.Regex("Slug", new BsonRegularExpression($"^\\s*{escaped}\\s*$", "i"));
        return Builders<BsonDocument>.Filter.Or(exact, trimmedCaseInsensitive);
    }

    private static string NormalizeSlug(string? rawSlug)
    {
        var value = (rawSlug ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // Accept deep links like https://t.me/nft/monkey-6
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath.Trim('/');
            if (!string.IsNullOrEmpty(path))
            {
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length >= 2 && string.Equals(segments[0], "nft", StringComparison.OrdinalIgnoreCase))
                {
                    return segments[^1].Trim();
                }
                return segments[^1].Trim();
            }
        }

        var marker = "/nft/";
        var markerIndex = value.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            var fromMarker = value[(markerIndex + marker.Length)..];
            var queryIndex = fromMarker.IndexOfAny(['?', '#']);
            return (queryIndex >= 0 ? fromMarker[..queryIndex] : fromMarker).Trim('/');
        }

        return value;
    }
}

