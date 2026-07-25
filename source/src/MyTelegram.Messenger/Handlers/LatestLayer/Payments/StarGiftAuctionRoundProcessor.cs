using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal static class StarGiftAuctionRoundProcessor
{
    private const string GiftsCollectionName = "eventflow-stargiftreadmodel";
    private const string BidsCollectionName = "eventflow-stargiftauctionbidreadmodel";
    private const string AcquiredCollectionName = "eventflow-stargiftauctionacquiredreadmodel";
    private const string SavedGiftsCollectionName = "eventflow-savedstargiftreadmodel";
    private const string BalanceCollectionName = "eventflow-userstarsbalancereadmodel";

    public static async Task ProcessDueRoundsAsync(
        IMongoDatabase mongoDatabase,
        IIdGenerator idGenerator,
        long giftId,
        int now)
    {
        var giftsCollection = mongoDatabase.GetCollection<BsonDocument>(GiftsCollectionName);
        var bidsCollection = mongoDatabase.GetCollection<BsonDocument>(BidsCollectionName);
        var acquiredCollection = mongoDatabase.GetCollection<BsonDocument>(AcquiredCollectionName);
        var savedGiftsCollection = mongoDatabase.GetCollection<BsonDocument>(SavedGiftsCollectionName);
        var balanceCollection = mongoDatabase.GetCollection<BsonDocument>(BalanceCollectionName);

        var lockToken = Guid.NewGuid().ToString("N");
        var lockFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("GiftId", giftId),
            Builders<BsonDocument>.Filter.Eq("Auction", true),
            Builders<BsonDocument>.Filter.Or(
                Builders<BsonDocument>.Filter.Exists("AuctionProcessLockUntil", false),
                Builders<BsonDocument>.Filter.Lt("AuctionProcessLockUntil", now)
            )
        );
        var lockUpdate = Builders<BsonDocument>.Update
            .Set("AuctionProcessLockToken", lockToken)
            .Set("AuctionProcessLockUntil", now + 30);

        var giftDoc = await giftsCollection.FindOneAndUpdateAsync(
            lockFilter,
            lockUpdate,
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After });

        if (giftDoc == null)
        {
            return;
        }

        try
        {
            var roundDuration = Math.Max(1, GetNullableInt(giftDoc, "RoundDuration") ?? 3600);
            var totalRounds = Math.Max(1, GetNullableInt(giftDoc, "TotalRounds") ?? 1);
            var currentRound = Math.Max(1, GetNullableInt(giftDoc, "CurrentRound") ?? 1);
            var giftsPerRound = Math.Max(1, GetNullableInt(giftDoc, "GiftsPerRound") ?? 1);
            var auctionStartDate = GetNullableInt(giftDoc, "AuctionStartDate") ?? now;
            var nextRoundAt = GetNullableInt(giftDoc, "NextRoundAt") ?? (auctionStartDate + currentRound * roundDuration);
            var configuredEndDate = GetNullableInt(giftDoc, "AuctionEndDate");
            var computedEndDate = auctionStartDate + totalRounds * roundDuration;
            var auctionEndDate = configuredEndDate.HasValue
                ? Math.Max(configuredEndDate.Value, computedEndDate)
                : computedEndDate;
            var giftsLeft = Math.Max(0, GetNullableInt(giftDoc, "GiftsLeft") ?? GetNullableInt(giftDoc, "AvailabilityRemains") ?? (giftsPerRound * totalRounds));
            var minBidAmount = GetNullableLong(giftDoc, "MinBidAmount") ?? 100;
            var lastGiftNum = Math.Max(0, GetNullableInt(giftDoc, "LastGiftNum") ?? 0);
            var soldCount = Math.Max(0, GetNullableInt(giftDoc, "AuctionSoldCount") ?? 0);
            var totalRevenue = Math.Max(0, GetNullableLong(giftDoc, "AuctionRevenue") ?? 0);

            if (now < nextRoundAt || currentRound > totalRounds || giftsLeft <= 0)
            {
                return;
            }

            var changed = false;
            while (now >= nextRoundAt && currentRound <= totalRounds && giftsLeft > 0)
            {
                changed = true;

                var activeBids = await GetActiveBidsAsync(bidsCollection, giftId);
                if (activeBids.Count > 0)
                {
                    var winnersCount = Math.Min(Math.Min(giftsPerRound, giftsLeft), activeBids.Count);
                    for (var i = 0; i < winnersCount; i++)
                    {
                        var winnerBid = activeBids[i];
                        var bidderUserId = GetLong(winnerBid, "UserId");
                        var recipientUserId = GetNullableLong(winnerBid, "RecipientUserId") ?? bidderUserId;
                        var bidAmount = GetLong(winnerBid, "BidAmount");
                        // Auction winnings are always visible in profile.
                        var hideName = false;
                        var bidMessage = GetNullableString(winnerBid, "Message");
                        var bidDate = GetNullableInt(winnerBid, "BidDate") ?? now;
                        lastGiftNum++;

                        await InsertSavedGiftForWinnerAsync(
                            savedGiftsCollection,
                            idGenerator,
                            giftDoc,
                            giftId,
                            now,
                            bidderUserId,
                            recipientUserId,
                            hideName,
                            bidMessage,
                            lastGiftNum);

                        await acquiredCollection.InsertOneAsync(new BsonDocument
                        {
                            { "GiftId", giftId },
                            { "PeerId", recipientUserId },
                            { "PeerType", "user" },
                            { "NameHidden", hideName },
                            { "Date", now },
                            { "BidAmount", bidAmount },
                            { "Round", currentRound },
                            { "Pos", i + 1 },
                            { "GiftNum", lastGiftNum },
                            { "MessageText", bidMessage ?? (BsonValue)BsonNull.Value }
                        });

                        var winnerUpdate = Builders<BsonDocument>.Update
                            .Set("Won", true)
                            .Set("Returned", true)
                            .Set("LastWonRound", currentRound)
                            .Set("GiftNum", lastGiftNum)
                            .Inc("AcquiredCount", 1);
                        await bidsCollection.UpdateOneAsync(
                            Builders<BsonDocument>.Filter.Eq("_id", winnerBid["_id"]),
                            winnerUpdate);

                        giftsLeft--;
                        soldCount++;
                        totalRevenue += bidAmount;
                    }
                }

                currentRound++;
                nextRoundAt += roundDuration;
            }

            var finished = giftsLeft <= 0 || currentRound > totalRounds || now >= auctionEndDate;
            if (finished)
            {
                changed = true;
                await RefundAllNonWinningBidsAsync(balanceCollection, bidsCollection, giftId, now);
            }

            if (!changed)
            {
                return;
            }

            var activeAfterRound = finished ? [] : await GetActiveBidsAsync(bidsCollection, giftId);
            var sortedTopBidders = activeAfterRound
                .Select(b => GetLong(b, "UserId"))
                .Distinct()
                .Take(10)
                .ToList();

            var bidLevels = new BsonArray();
            for (var i = 0; i < Math.Min(activeAfterRound.Count, giftsPerRound); i++)
            {
                var bid = activeAfterRound[i];
                bidLevels.Add(new BsonDocument
                {
                    { "Pos", i + 1 },
                    { "Amount", GetLong(bid, "BidAmount") },
                    { "Date", GetNullableInt(bid, "BidDate") ?? now }
                });
            }

            long newMinBid = minBidAmount;
            if (!finished && activeAfterRound.Count >= giftsPerRound)
            {
                var lowestWinningBid = GetLong(activeAfterRound[giftsPerRound - 1], "BidAmount");
                newMinBid = Math.Max(minBidAmount, lowestWinningBid + 1);
            }

            var averagePrice = soldCount > 0 ? totalRevenue / soldCount : GetNullableLong(giftDoc, "AuctionAveragePrice") ?? 0;
            var newAuctionEndDate = finished ? now : Math.Max(auctionEndDate, nextRoundAt);
            var displayRound = finished ? totalRounds : Math.Min(currentRound, totalRounds);
            var newAuctionVersion = (GetNullableInt(giftDoc, "AuctionVersion") ?? 0) + 1;

            var finalUpdate = Builders<BsonDocument>.Update
                .Set("TopBidders", new BsonArray(sortedTopBidders))
                .Set("BidLevels", bidLevels)
                .Set("MinBidAmount", newMinBid)
                .Set("CurrentRound", displayRound)
                .Set("TotalRounds", totalRounds)
                .Set("RoundDuration", roundDuration)
                .Set("NextRoundAt", nextRoundAt)
                .Set("AuctionStartDate", auctionStartDate)
                .Set("AuctionEndDate", newAuctionEndDate)
                .Set("GiftsLeft", giftsLeft)
                .Set("AvailabilityRemains", giftsLeft)
                .Set("LastGiftNum", lastGiftNum)
                .Set("AuctionSoldCount", soldCount)
                .Set("AuctionRevenue", totalRevenue)
                .Set("AuctionAveragePrice", averagePrice)
                .Set("SoldOut", giftsLeft <= 0)
                .Set("AuctionVersion", newAuctionVersion);

            await giftsCollection.UpdateOneAsync(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("GiftId", giftId),
                    Builders<BsonDocument>.Filter.Eq("AuctionProcessLockToken", lockToken)),
                finalUpdate);
        }
        finally
        {
            await giftsCollection.UpdateOneAsync(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("GiftId", giftId),
                    Builders<BsonDocument>.Filter.Eq("AuctionProcessLockToken", lockToken)),
                Builders<BsonDocument>.Update
                    .Unset("AuctionProcessLockToken")
                    .Unset("AuctionProcessLockUntil"));
        }
    }

    private static async Task<List<BsonDocument>> GetActiveBidsAsync(
        IMongoCollection<BsonDocument> bidsCollection,
        long giftId)
    {
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("GiftId", giftId),
            Builders<BsonDocument>.Filter.Eq("Returned", false));

        return await bidsCollection.Find(filter)
            .Sort(Builders<BsonDocument>.Sort.Descending("BidAmount").Ascending("BidDate"))
            .Limit(500)
            .ToListAsync();
    }

    private static async Task InsertSavedGiftForWinnerAsync(
        IMongoCollection<BsonDocument> savedGiftsCollection,
        IIdGenerator idGenerator,
        BsonDocument giftDoc,
        long giftId,
        int now,
        long bidderUserId,
        long recipientUserId,
        bool hideName,
        string? message,
        int giftNum)
    {
        var convertStars = GetNullableLong(giftDoc, "ConvertStars") ?? 0;
        var canUpgrade = StarGiftUpgradeStateHelper.IsUpgradableGift(giftDoc);
        var savedGiftId = await idGenerator.NextIdAsync(IdType.SavedStarGiftId, recipientUserId);
        var msgId = await idGenerator.NextIdAsync(IdType.MessageId, recipientUserId);

        var savedGiftDoc = new BsonDocument
        {
            { "SavedId", (long)savedGiftId },
            { "OwnerUserId", recipientUserId },
            { "SenderUserId", bidderUserId },
            { "FromUserId", hideName ? BsonNull.Value : bidderUserId },
            { "GiftId", giftId },
            { "Date", now },
            { "MsgId", msgId },
                            { "NameHidden", hideName },
            { "Saved", true },
            { "PinnedToTop", false },
            { "Converted", false },
            { "Upgraded", false },
            { "Refunded", false },
            { "UpgradeSeparate", false },
            { "PrepaidUpgrade", false },
            { "CanUpgrade", canUpgrade },
            { "ConvertStars", convertStars },
            { "UpgradeStars", BsonNull.Value },
            { "PrepaidUpgradeHash", BsonNull.Value },
            { "Message", message ?? (BsonValue)BsonNull.Value },
            { "MessageEntities", BsonNull.Value },
            { "GiftNum", giftNum },
            { "AuctionAcquired", true }
        };

        await savedGiftsCollection.InsertOneAsync(savedGiftDoc);
    }

    private static async Task RefundAllNonWinningBidsAsync(
        IMongoCollection<BsonDocument> balanceCollection,
        IMongoCollection<BsonDocument> bidsCollection,
        long giftId,
        int now)
    {
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("GiftId", giftId),
            Builders<BsonDocument>.Filter.Eq("Returned", false));

        var bidsToRefund = await bidsCollection.Find(filter).ToListAsync();
        foreach (var bid in bidsToRefund)
        {
            var userId = GetLong(bid, "UserId");
            var bidAmount = GetLong(bid, "BidAmount");
            if (bidAmount > 0)
            {
                await balanceCollection.UpdateOneAsync(
                    Builders<BsonDocument>.Filter.Eq("UserId", userId),
                    Builders<BsonDocument>.Update
                        .Inc("Balance", bidAmount)
                        .Set("LastUpdated", DateTime.UtcNow),
                    new UpdateOptions { IsUpsert = true });
            }

            await bidsCollection.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", bid["_id"]),
                Builders<BsonDocument>.Update
                    .Set("Returned", true)
                    .Set("ReturnedDate", now));
        }
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

    private static long GetLong(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull) return 0;
        var value = doc[field];
        return value.IsInt64 ? value.AsInt64 : value.AsInt32;
    }
}
