using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

/// <summary>
/// Get the state of a star gift auction.
/// <para><c>See <a href="" /> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ] [Bot ] [Anonymous ]
/// </remarks>
internal sealed class GetStarGiftAuctionStateHandler(
    IMongoDatabase mongoDatabase,
    ILayeredService<IDocumentConverter> documentConverterService,
    IUserConverterService userConverterService,
    IIdGenerator idGenerator)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetStarGiftAuctionState, MyTelegram.Schema.Payments.IStarGiftAuctionState>, IObjectHandler
{
    protected override async Task<MyTelegram.Schema.Payments.IStarGiftAuctionState> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Payments.RequestGetStarGiftAuctionState obj)
    {
        // Get gift ID from input
        long giftId = obj.Auction switch
        {
            TInputStarGiftAuction auction => auction.GiftId,
            TInputStarGiftAuctionSlug slugAuction => await GetGiftIdBySlugAsync(slugAuction.Slug),
            _ => throw new RpcException(new RpcError(400, "AUCTION_INVALID"))
        };
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await StarGiftAuctionRoundProcessor.ProcessDueRoundsAsync(mongoDatabase, idGenerator, giftId, now);

        // Get gift from database
        var giftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stargiftreadmodel");
        var giftDoc = await giftsCollection.Find(
            Builders<BsonDocument>.Filter.Eq("GiftId", giftId)
        ).FirstOrDefaultAsync();

        if (giftDoc == null)
        {
            throw new RpcException(new RpcError(400, "GIFT_NOT_FOUND"));
        }

        // Check if this is an auction gift
        if (!giftDoc.GetValue("Auction", false).AsBoolean)
        {
            throw new RpcException(new RpcError(400, "GIFT_NOT_AUCTION"));
        }

        // Build the gift object
        var documentsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");
        var stickerId = GetLong(giftDoc, "StickerId");
        var docDoc = await documentsCollection.Find(
            Builders<BsonDocument>.Filter.Eq("DocumentId", stickerId)
        ).FirstOrDefaultAsync();

        var documentConverter = documentConverterService.GetConverter(input.Layer);
        IDocument sticker = docDoc != null
            ? ConvertDocument(docDoc)
            : new TDocumentEmpty { Id = stickerId };

        var gift = BuildStarGift(giftDoc, sticker);

        var auctionVersion = GetNullableInt(giftDoc, "AuctionVersion") ?? 1;
        var auctionState = obj.Version > 0 && obj.Version == auctionVersion
            ? new TStarGiftAuctionStateNotModified()
            : BuildAuctionState(giftDoc);

        // Build user state (for current user)
        var userState = await BuildUserStateAsync(input.UserId, giftId);

        // Get timeout (seconds until next state change)
        var timeout = CalculateTimeout(giftDoc);

        var userIds = CollectAuctionUserIds(giftDoc, auctionState, userState);
        var users = new TVector<IUser>();
        if (userIds.Count > 0)
        {
            var userList = await userConverterService.GetUserListAsync(input, userIds.ToList(), true, true, input.Layer);
            foreach (var user in userList)
            {
                users.Add(user);
            }
        }

        return new MyTelegram.Schema.Payments.TStarGiftAuctionState
        {
            Gift = gift,
            State = auctionState,
            UserState = userState,
            Timeout = timeout,
            Users = users,
            Chats = []
        };
    }

    private static HashSet<long> CollectAuctionUserIds(BsonDocument giftDoc, MyTelegram.Schema.IStarGiftAuctionState auctionState, MyTelegram.Schema.IStarGiftAuctionUserState userState)
    {
        var userIds = new HashSet<long>();

        var releasedByPeerId = GetNullableLong(giftDoc, "ReleasedByPeerId");
        var releasedByPeerType = GetNullableInt(giftDoc, "ReleasedByPeerType");
        if (releasedByPeerId.HasValue && releasedByPeerType == 1)
        {
            userIds.Add(releasedByPeerId.Value);
        }

        if (auctionState is MyTelegram.Schema.TStarGiftAuctionState activeState)
        {
            foreach (var bidderId in activeState.TopBidders)
            {
                userIds.Add(bidderId);
            }
        }

        AddUserIdFromPeer(userIds, userState.BidPeer);

        return userIds;
    }

    private static void AddUserIdFromPeer(HashSet<long> userIds, IPeer? peer)
    {
        if (peer is TPeerUser userPeer)
        {
            userIds.Add(userPeer.UserId);
        }
    }

    private async Task<long> GetGiftIdBySlugAsync(string slug)
    {
        var giftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stargiftreadmodel");
        var giftDoc = await giftsCollection.Find(
            Builders<BsonDocument>.Filter.Eq("AuctionSlug", slug)
        ).FirstOrDefaultAsync();

        if (giftDoc == null)
        {
            throw new RpcException(new RpcError(400, "AUCTION_SLUG_INVALID"));
        }

        return GetLong(giftDoc, "GiftId");
    }

    private static IStarGift BuildStarGift(BsonDocument g, IDocument sticker)
    {
        return new TStarGift
        {
            Id = GetLong(g, "GiftId"),
            Limited = g.GetValue("Limited", false).AsBoolean,
            SoldOut = g.GetValue("SoldOut", false).AsBoolean,
            Birthday = g.GetValue("Birthday", false).AsBoolean,
            RequirePremium = g.GetValue("RequirePremium", false).AsBoolean,
            LimitedPerUser = g.GetValue("LimitedPerUser", false).AsBoolean,
            PeerColorAvailable = g.GetValue("PeerColorAvailable", false).AsBoolean,
            Auction = g.GetValue("Auction", false).AsBoolean,
            Sticker = sticker,
            Stars = GetLong(g, "Stars"),
            ConvertStars = GetLong(g, "ConvertStars"),
            AvailabilityRemains = GetNullableInt(g, "AvailabilityRemains"),
            AvailabilityTotal = GetNullableInt(g, "AvailabilityTotal"),
            AvailabilityResale = GetNullableLong(g, "AvailabilityResale"),
            FirstSaleDate = GetNullableInt(g, "FirstSaleDate"),
            LastSaleDate = GetNullableInt(g, "LastSaleDate"),
            UpgradeStars = GetNullableLong(g, "UpgradeStars"),
            ResellMinStars = GetNullableLong(g, "ResellMinStars"),
            Title = GetNullableString(g, "Title"),
            ReleasedBy = GetReleasedByPeer(g),
            PerUserTotal = GetNullableInt(g, "PerUserTotal"),
            PerUserRemains = GetNullableInt(g, "PerUserRemains"),
            LockedUntilDate = GetNullableInt(g, "LockedUntilDate"),
            AuctionSlug = GetNullableString(g, "AuctionSlug"),
            GiftsPerRound = GetNullableInt(g, "GiftsPerRound"),
            AuctionStartDate = GetNullableInt(g, "AuctionStartDate"),
            UpgradeVariants = GetNullableInt(g, "UpgradeVariants"),
            Background = GetStarGiftBackground(g)
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

    private static MyTelegram.Schema.IStarGiftAuctionState BuildAuctionState(BsonDocument g)
    {
        var startDate = GetNullableInt(g, "AuctionStartDate");
        var endDate = GetNullableInt(g, "AuctionEndDate");
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // If auction is finished
        if (endDate.HasValue && now > endDate.Value)
        {
            return new TStarGiftAuctionStateFinished
            {
                StartDate = startDate ?? 0,
                EndDate = endDate.Value,
                AveragePrice = GetNullableLong(g, "AuctionAveragePrice") ?? 0,
                ListedCount = GetNullableInt(g, "ListedCount"),
                FragmentListedCount = GetNullableInt(g, "FragmentListedCount"),
                FragmentListedUrl = GetNullableString(g, "FragmentListedUrl")
            };
        }

        // Active auction state
        var rounds = new TVector<MyTelegram.Schema.IStarGiftAuctionRound>();
        var totalRounds = GetNullableInt(g, "TotalRounds") ?? 1;
        var currentRound = GetNullableInt(g, "CurrentRound") ?? 1;
        var roundDuration = GetNullableInt(g, "RoundDuration") ?? 3600;
        var extendTop = GetNullableInt(g, "ExtendTop");
        var extendWindow = GetNullableInt(g, "ExtendWindow");

        for (var i = 1; i <= totalRounds; i++)
        {
            // Use TStarGiftAuctionRoundExtendable if extend parameters are set
            if (extendTop.HasValue && extendWindow.HasValue)
            {
                rounds.Add(new TStarGiftAuctionRoundExtendable
                {
                    Num = i,
                    Duration = roundDuration,
                    ExtendTop = extendTop.Value,
                    ExtendWindow = extendWindow.Value
                });
            }
            else
            {
                rounds.Add(new TStarGiftAuctionRound
                {
                    Num = i,
                    Duration = roundDuration
                });
            }
        }

        // Build bid levels with dates
        var bidLevels = BuildBidLevels(g);

        // Get top bidders from document
        var topBidders = GetTopBidders(g);

        // Use full namespace to avoid ambiguity
        return new MyTelegram.Schema.TStarGiftAuctionState
        {
            Version = GetNullableInt(g, "AuctionVersion") ?? 1,
            StartDate = startDate ?? now,
            EndDate = endDate ?? (now + 86400),
            MinBidAmount = GetNullableLong(g, "MinBidAmount") ?? 100,
            BidLevels = bidLevels,
            TopBidders = topBidders,
            NextRoundAt = CalculateNextRoundAt(g),
            LastGiftNum = GetNullableInt(g, "LastGiftNum") ?? 0,
            GiftsLeft = GetNullableInt(g, "GiftsLeft") ?? GetNullableInt(g, "AvailabilityRemains") ?? 0,
            CurrentRound = currentRound,
            TotalRounds = totalRounds,
            Rounds = rounds
        };
    }

    private static TVector<MyTelegram.Schema.IAuctionBidLevel> BuildBidLevels(BsonDocument g)
    {
        var bidLevels = new TVector<MyTelegram.Schema.IAuctionBidLevel>();
        var minBid = GetNullableLong(g, "MinBidAmount") ?? 100;
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Check if we have custom bid levels in the document
        if (g.Contains("BidLevels") && !g["BidLevels"].IsBsonNull && g["BidLevels"].IsBsonArray)
        {
            var levels = g["BidLevels"].AsBsonArray;
            foreach (var level in levels)
            {
                if (level.IsBsonDocument)
                {
                    var levelDoc = level.AsBsonDocument;
                    bidLevels.Add(new TAuctionBidLevel
                    {
                        Pos = GetNullableInt(levelDoc, "Pos") ?? bidLevels.Count + 1,
                        Amount = GetNullableLong(levelDoc, "Amount") ?? minBid * (bidLevels.Count + 1),
                        Date = GetNullableInt(levelDoc, "Date") ?? now
                    });
                }
            }
        }

        // Default bid levels if none specified
        if (bidLevels.Count == 0)
        {
            bidLevels.Add(new TAuctionBidLevel { Pos = 1, Amount = minBid, Date = now });
            bidLevels.Add(new TAuctionBidLevel { Pos = 2, Amount = minBid * 2, Date = now });
            bidLevels.Add(new TAuctionBidLevel { Pos = 3, Amount = minBid * 3, Date = now });
        }

        return bidLevels;
    }

    private static TVector<long> GetTopBidders(BsonDocument g)
    {
        var topBidders = new TVector<long>();

        if (g.Contains("TopBidders") && !g["TopBidders"].IsBsonNull && g["TopBidders"].IsBsonArray)
        {
            var bidders = g["TopBidders"].AsBsonArray;
            foreach (var bidder in bidders)
            {
                if (bidder.IsInt64)
                    topBidders.Add(bidder.AsInt64);
                else if (bidder.IsInt32)
                    topBidders.Add(bidder.AsInt32);
            }
        }

        return topBidders;
    }

    private async Task<MyTelegram.Schema.IStarGiftAuctionUserState> BuildUserStateAsync(long userId, long giftId)
    {
        // Try to get user's bid from database
        var bidsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stargiftauctionbidreadmodel");
        var userBid = await bidsCollection.Find(
            Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("UserId", userId),
                Builders<BsonDocument>.Filter.Eq("GiftId", giftId)
            )
        ).SortByDescending(b => b["BidDate"]).FirstOrDefaultAsync();

        if (userBid == null)
        {
            return new TStarGiftAuctionUserState
            {
                AcquiredCount = 0
            };
        }

        return new TStarGiftAuctionUserState
        {
            BidAmount = GetNullableLong(userBid, "BidAmount"),
            BidDate = GetNullableInt(userBid, "BidDate"),
            MinBidAmount = GetNullableLong(userBid, "MinBidAmount"),
            BidPeer = GetBidPeer(userBid),
            Returned = userBid.GetValue("Returned", false).AsBoolean,
            AcquiredCount = GetNullableInt(userBid, "AcquiredCount") ?? 0
        };
    }

    private static IPeer? GetBidPeer(BsonDocument doc)
    {
        var peerId = GetNullableLong(doc, "BidPeerId");
        var peerType = GetNullableInt(doc, "BidPeerType");

        if (!peerId.HasValue)
            return null;

        // Default to user if no type specified
        var type = peerType ?? 1;
        return type switch
        {
            1 => new TPeerUser { UserId = peerId.Value },
            2 => new TPeerChat { ChatId = peerId.Value },
            3 => new TPeerChannel { ChannelId = peerId.Value },
            _ => new TPeerUser { UserId = peerId.Value }
        };
    }

    private static int CalculateTimeout(BsonDocument g)
    {
        var nextRoundAt = GetNullableInt(g, "NextRoundAt");
        var endDate = GetNullableInt(g, "AuctionEndDate");
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (nextRoundAt.HasValue && nextRoundAt.Value > now)
        {
            return nextRoundAt.Value - now;
        }

        if (endDate.HasValue && endDate.Value > now)
        {
            return endDate.Value - now;
        }

        return 300; // Default 5 minutes
    }

    private static int CalculateNextRoundAt(BsonDocument g)
    {
        var nextRoundAt = GetNullableInt(g, "NextRoundAt");
        if (nextRoundAt.HasValue)
        {
            return nextRoundAt.Value;
        }

        var startDate = GetNullableInt(g, "AuctionStartDate");
        var roundDuration = GetNullableInt(g, "RoundDuration") ?? 3600;
        var currentRound = GetNullableInt(g, "CurrentRound") ?? 1;

        if (startDate.HasValue)
        {
            return startDate.Value + (currentRound * roundDuration);
        }

        return (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + roundDuration;
    }

    private static IDocument ConvertDocument(BsonDocument doc)
    {
        return new TDocument
        {
            Id = GetLong(doc, "DocumentId"),
            AccessHash = GetLong(doc, "AccessHash"),
            Date = doc["Date"].AsInt32,
            MimeType = doc["MimeType"].AsString,
            Size = GetLong(doc, "Size"),
            DcId = doc["DcId"].AsInt32,
            FileReference = doc.Contains("FileReference") && !doc["FileReference"].IsBsonNull
                ? doc["FileReference"].AsByteArray
                : Array.Empty<byte>(),
            Attributes = new TVector<IDocumentAttribute>()
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
}
