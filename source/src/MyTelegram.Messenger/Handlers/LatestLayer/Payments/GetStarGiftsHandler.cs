using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Schema.Payments;
using MongoDocumentReadModel = MyTelegram.ReadModel.MongoDB.DocumentReadModel;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

/// <summary>
/// Get a list of available <a href="https://corefork.telegram.org/api/gifts">gifts, see here »</a> for more info.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.getStarGifts"/> </c></para>
/// </summary>
internal sealed class GetStarGiftsHandler(
    IMongoDatabase mongoDatabase,
    ILayeredService<IDocumentConverter> documentConverterService,
    IUserConverterService userConverterService)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetStarGifts, MyTelegram.Schema.Payments.IStarGifts>
{
    protected override async Task<MyTelegram.Schema.Payments.IStarGifts> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Payments.RequestGetStarGifts obj)
    {
        // Get collections
        var giftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stargiftreadmodel");
        var documentsCollection = mongoDatabase.GetCollection<MongoDocumentReadModel>("eventflow-documentreadmodel");

        // Get all star gifts
        var giftDocs = await giftsCollection.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();

        if (giftDocs.Count == 0)
        {
            // Check hash for not modified
            if (obj.Hash != 0)
            {
                return new TStarGiftsNotModified();
            }
            return new TStarGifts { Hash = 0, Gifts = new TVector<IStarGift>(), Chats = new TVector<IChat>(), Users = new TVector<IUser>() };
        }

        // Sort gifts: Limited (not sold out) -> Regular (not limited, not sold out) -> Sold out
        giftDocs = giftDocs
            .OrderBy(GetGiftSortOrder)
            .ThenBy(g => GetLong(g, "GiftId"))   // Secondary sort by GiftId for consistency
            .ToList();

        // Calculate hash first to check if modified
        var hash = CalculateHash(giftDocs);
        if (obj.Hash != 0 && obj.Hash == hash)
        {
            return new TStarGiftsNotModified();
        }

        // Get sticker IDs and fetch documents
        var stickerIds = giftDocs.Select(g => GetLong(g, "StickerId")).Distinct().ToList();
        var docFilter = Builders<MongoDocumentReadModel>.Filter.In(p => p.DocumentId, stickerIds);
        var docDocs = await documentsCollection.Find(docFilter).ToListAsync();
        var documentMap = docDocs.ToDictionary(d => d.DocumentId);

        var documentConverter = documentConverterService.GetConverter(input.Layer);
        var gifts = new TVector<IStarGift>();
        var userIds = new HashSet<long>();

        foreach (var g in giftDocs)
        {
            var stickerId = GetLong(g, "StickerId");
            IDocument sticker;

            if (documentMap.TryGetValue(stickerId, out var documentReadModel))
            {
                sticker = documentConverter.ToDocument(documentReadModel);
                if (sticker is TDocument document)
                {
                    document.Attributes ??= [];
                    document.MimeType ??= "application/octet-stream";
                }
            }
            else
            {
                sticker = new TDocumentEmpty { Id = stickerId };
            }

            // Collect ReleasedBy user IDs
            var releasedByPeerId = GetNullableLong(g, "ReleasedByPeerId");
            var releasedByPeerType = GetNullableInt(g, "ReleasedByPeerType");
            if (releasedByPeerId.HasValue && releasedByPeerType == 1)
            {
                userIds.Add(releasedByPeerId.Value);
            }

            var availabilityRemains = GetNullableInt(g, "AvailabilityRemains");
            var availabilityTotal = GetNullableInt(g, "AvailabilityTotal");
            var limited = IsLimitedGift(g, availabilityRemains, availabilityTotal);
            var soldOut = IsSoldOutGift(g, limited, availabilityRemains);
            if (limited)
            {
                availabilityRemains ??= 0;
                availabilityTotal ??= 0;
            }

            var firstSaleDate = GetNullableInt(g, "FirstSaleDate");
            var lastSaleDate = GetNullableInt(g, "LastSaleDate");
            if (soldOut)
            {
                firstSaleDate ??= 0;
                lastSaleDate ??= 0;
            }

            var perUserTotal = GetNullableInt(g, "PerUserTotal");
            var perUserRemains = GetNullableInt(g, "PerUserRemains");
            var limitedPerUser = GetBool(g, "LimitedPerUser");
            if (limitedPerUser)
            {
                perUserTotal ??= 0;
                perUserRemains ??= 0;
            }

            var auctionSlug = GetNullableString(g, "AuctionSlug");
            var giftsPerRound = GetNullableInt(g, "GiftsPerRound");
            var auctionStartDate = GetNullableInt(g, "AuctionStartDate");
            var auction = GetBool(g, "Auction");
            if (auction)
            {
                auctionSlug ??= string.Empty;
                giftsPerRound ??= 0;
                auctionStartDate ??= 0;
            }

            var tGift = new TStarGift
            {
                Id = GetLong(g, "GiftId"),
                Limited = limited,
                SoldOut = soldOut,
                Birthday = GetBool(g, "Birthday"),
                RequirePremium = GetBool(g, "RequirePremium"),
                LimitedPerUser = limitedPerUser,
                PeerColorAvailable = GetBool(g, "PeerColorAvailable"),
                Auction = auction,
                Sticker = sticker,
                Stars = GetLong(g, "Stars"),
                ConvertStars = GetLong(g, "ConvertStars"),
                AvailabilityRemains = availabilityRemains,
                AvailabilityTotal = availabilityTotal,
                AvailabilityResale = GetNullableLong(g, "AvailabilityResale"),
                FirstSaleDate = firstSaleDate,
                LastSaleDate = lastSaleDate,
                UpgradeStars = GetNullableLong(g, "UpgradeStars"),
                ResellMinStars = GetNullableLong(g, "ResellMinStars"),
                Title = GetNullableString(g, "Title"),
                ReleasedBy = GetReleasedByPeer(g),
                PerUserTotal = perUserTotal,
                PerUserRemains = perUserRemains,
                LockedUntilDate = GetNullableInt(g, "LockedUntilDate"),
                AuctionSlug = auctionSlug,
                GiftsPerRound = giftsPerRound,
                AuctionStartDate = auctionStartDate,
                UpgradeVariants = GetNullableInt(g, "UpgradeVariants"),
                Background = GetStarGiftBackground(g)
            };

            gifts.Add(tGift);
        }

        // Get users via converter service
        var users = new TVector<IUser>();
        if (userIds.Count > 0)
        {
            var userList = await userConverterService.GetUserListAsync(input, userIds.ToList(), true, true, input.Layer);
            foreach (var user in userList)
            {
                users.Add(user);
            }
        }

        return new TStarGifts { Hash = hash, Gifts = gifts, Chats = new TVector<IChat>(), Users = users };
    }

    private static long GetLong(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull) return 0;
        var value = doc[field];
        return value.IsInt64 ? value.AsInt64 : value.AsInt32;
    }

    private static bool GetBool(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull) return false;
        return doc[field].AsBoolean;
    }

    private static int GetGiftSortOrder(BsonDocument giftDoc)
    {
        var availabilityRemains = GetNullableInt(giftDoc, "AvailabilityRemains");
        var availabilityTotal = GetNullableInt(giftDoc, "AvailabilityTotal");
        var limited = IsLimitedGift(giftDoc, availabilityRemains, availabilityTotal);
        var soldOut = IsSoldOutGift(giftDoc, limited, availabilityRemains);

        if (soldOut)
        {
            return 2;
        }

        if (limited)
        {
            return 0;
        }

        return 1;
    }

    private static bool IsLimitedGift(BsonDocument giftDoc, int? availabilityRemains, int? availabilityTotal)
    {
        if (giftDoc.Contains("Limited") && !giftDoc["Limited"].IsBsonNull)
        {
            return giftDoc["Limited"].AsBoolean;
        }

        // Legacy fallback for old docs without explicit Limited flag.
        return availabilityRemains.HasValue || availabilityTotal.HasValue;
    }

    private static bool IsSoldOutGift(BsonDocument giftDoc, bool limited, int? availabilityRemains)
    {
        if (giftDoc.Contains("SoldOut") && !giftDoc["SoldOut"].IsBsonNull)
        {
            return giftDoc["SoldOut"].AsBoolean;
        }

        // Legacy fallback for old docs without explicit SoldOut flag.
        return limited && availabilityRemains.HasValue && availabilityRemains.Value <= 0;
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

    private static IStarGiftBackground? GetStarGiftBackground(BsonDocument doc)
    {
        var centerColor = GetNullableInt(doc, "BackgroundCenterColor");
        var edgeColor = GetNullableInt(doc, "BackgroundEdgeColor");
        var textColor = GetNullableInt(doc, "BackgroundTextColor");

        if (!centerColor.HasValue || !edgeColor.HasValue || !textColor.HasValue)
            return null;

        return new TStarGiftBackground
        {
            CenterColor = centerColor.Value,
            EdgeColor = edgeColor.Value,
            TextColor = textColor.Value
        };
    }

    private static IPeer? GetReleasedByPeer(BsonDocument doc)
    {
        var peerId = GetNullableLong(doc, "ReleasedByPeerId");
        var peerType = GetNullableInt(doc, "ReleasedByPeerType");

        if (!peerId.HasValue || !peerType.HasValue)
            return null;

        return peerType.Value switch
        {
            1 => new TPeerUser { UserId = peerId.Value },
            2 => new TPeerChat { ChatId = peerId.Value },
            3 => new TPeerChannel { ChannelId = peerId.Value },
            _ => null
        };
    }

    private static int CalculateHash(List<BsonDocument> gifts)
    {
        if (gifts.Count == 0) return 0;
        unchecked
        {
            var hash = 0;
            foreach (var g in gifts.OrderBy(x => GetLong(x, "GiftId")))
            {
                var giftId = GetLong(g, "GiftId");
                hash = (hash * 31) ^ (int)(giftId & 0xFFFFFFFF);
                hash = (hash * 31) ^ (int)(giftId >> 32);
                
                // Include dynamic fields in hash so client gets updates
                var remains = GetNullableInt(g, "AvailabilityRemains") ?? 0;
                var availabilityRemains = GetNullableInt(g, "AvailabilityRemains");
                var availabilityTotal = GetNullableInt(g, "AvailabilityTotal");
                var limited = IsLimitedGift(g, availabilityRemains, availabilityTotal);
                var soldOut = IsSoldOutGift(g, limited, availabilityRemains) ? 1 : 0;
                var firstSale = GetNullableInt(g, "FirstSaleDate") ?? 0;
                var lastSale = GetNullableInt(g, "LastSaleDate") ?? 0;
                
                hash = (hash * 31) ^ remains;
                hash = (hash * 31) ^ soldOut;
                hash = (hash * 31) ^ firstSale;
                hash = (hash * 31) ^ lastSale;
            }
            return hash;
        }
    }
}
