using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Fetch info about specific <a href="https://corefork.telegram.org/api/gifts">gifts</a> owned by a peer we control.Note that unlike what the name suggests, the method can be used to fetch both "saved" and "unsaved" gifts (aka gifts both pinned and not pinned to the profile).
/// Possible errors
/// Code Type Description
/// 400 SAVED_ID_EMPTY The passed inputSavedStarGiftChat.saved_id is empty.
/// 400 STARGIFT_SLUG_INVALID The specified gift slug is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.getSavedStarGift"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetSavedStarGiftHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IUserConverterService userConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetSavedStarGift, MyTelegram.Schema.Payments.ISavedStarGifts>
{
    protected override async Task<MyTelegram.Schema.Payments.ISavedStarGifts> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetSavedStarGift obj)
    {
        var savedGiftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-savedstargiftreadmodel");
        var giftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stargiftreadmodel");
        var documentsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");

        var savedGiftDocs = new List<BsonDocument>();
        var userIds = new HashSet<long>();

        // Process each input saved star gift
        foreach (var inputGift in obj.Stargift)
        {
            BsonDocument? savedGiftDoc = null;

            switch (inputGift)
            {
                case TInputSavedStarGiftUser userGift:
                    // First try to find by MsgId
                    var userFilter = Builders<BsonDocument>.Filter.And(
                        Builders<BsonDocument>.Filter.Eq("OwnerUserId", input.UserId),
                        Builders<BsonDocument>.Filter.Eq("MsgId", userGift.MsgId)
                    );
                    savedGiftDoc = await savedGiftsCollection.Find(userFilter).FirstOrDefaultAsync();
                    
                    // Fallback: try to find by SavedId (client may send SavedId as MsgId)
                    if (savedGiftDoc == null)
                    {
                        userFilter = Builders<BsonDocument>.Filter.And(
                            Builders<BsonDocument>.Filter.Eq("OwnerUserId", input.UserId),
                            Builders<BsonDocument>.Filter.Eq("SavedId", (long)userGift.MsgId)
                        );
                        savedGiftDoc = await savedGiftsCollection.Find(userFilter).FirstOrDefaultAsync();
                    }
                    break;

                case TInputSavedStarGiftChat chatGift:
                    // Get gift by peer and saved_id
                    var chatPeer = peerHelper.GetPeer(chatGift.Peer, input.UserId);
                    if (chatGift.SavedId == 0)
                    {
                        RpcErrors.RpcErrors400.SavedIdEmpty.ThrowRpcError();
                    }
                    var chatFilter = Builders<BsonDocument>.Filter.And(
                        Builders<BsonDocument>.Filter.Eq("OwnerUserId", chatPeer.PeerId),
                        Builders<BsonDocument>.Filter.Eq("SavedId", chatGift.SavedId)
                    );
                    savedGiftDoc = await savedGiftsCollection.Find(chatFilter).FirstOrDefaultAsync();
                    break;

                case TInputSavedStarGiftSlug slugGift:
                    // Get gift by slug (for unique/collectible gifts)
                    var slugFilter = Builders<BsonDocument>.Filter.Eq("Slug", slugGift.Slug);
                    savedGiftDoc = await savedGiftsCollection.Find(slugFilter).FirstOrDefaultAsync();
                    if (savedGiftDoc == null)
                    {
                        RpcErrors.RpcErrors400.StargiftSlugInvalid.ThrowRpcError();
                    }
                    break;
            }

            if (savedGiftDoc != null)
            {
                // Check visibility: unsaved gifts (Saved = false) are only visible to owner
                var ownerUserId = GetLong(savedGiftDoc, "OwnerUserId");
                var isSaved = savedGiftDoc.GetValue("Saved", false).AsBoolean;
                var isOwner = input.UserId == ownerUserId;

                // Skip unsaved gifts if requester is not the owner
                if (!isSaved && !isOwner)
                {
                    continue;
                }

                savedGiftDocs.Add(savedGiftDoc);
                userIds.Add(ownerUserId);
                if (savedGiftDoc.Contains("FromUserId") && !savedGiftDoc["FromUserId"].IsBsonNull)
                {
                    userIds.Add(GetLong(savedGiftDoc, "FromUserId"));
                }
            }
        }

        if (savedGiftDocs.Count == 0)
        {
            return new TSavedStarGifts { Count = 0, Chats = [], Gifts = [], Users = [] };
        }

        // Get gift definitions
        var giftIds = savedGiftDocs.Select(g => GetLong(g, "GiftId")).Distinct().ToList();
        var giftFilter = Builders<BsonDocument>.Filter.In("GiftId", giftIds);
        var giftDocs = await giftsCollection.Find(giftFilter).ToListAsync();
        var giftMap = giftDocs.ToDictionary(g => GetLong(g, "GiftId"));
        var craftUpgradableGiftIds = await GetCraftUpgradableGiftIdsAsync(savedGiftsCollection, input.UserId, giftMap);

        // Get stickers
        var stickerIds = giftDocs.Select(g => GetLong(g, "StickerId")).Distinct().ToList();
        var docFilter = Builders<BsonDocument>.Filter.In("DocumentId", stickerIds);
        var stickerDocs = await documentsCollection.Find(docFilter).ToListAsync();
        var stickerMap = stickerDocs.ToDictionary(d => GetLong(d, "DocumentId"));

        // Build saved gifts
        var gifts = new TVector<ISavedStarGift>();
        foreach (var savedGiftDoc in savedGiftDocs)
        {
            var giftId = GetLong(savedGiftDoc, "GiftId");
            if (!giftMap.TryGetValue(giftId, out var giftDoc))
            {
                continue;
            }

            await StarGiftUpgradeStateHelper.SyncCanUpgradeAsync(savedGiftsCollection, savedGiftDoc, giftDoc);

            var stickerId = GetLong(giftDoc, "StickerId");
            IDocument sticker = stickerMap.TryGetValue(stickerId, out var stickerDoc)
                ? ConvertDocument(stickerDoc)
                : new TDocumentEmpty { Id = stickerId };

            var tGift = new TStarGift
            {
                Id = giftId,
                Limited = giftDoc.GetValue("Limited", false).AsBoolean,
                SoldOut = giftDoc.GetValue("SoldOut", false).AsBoolean,
                Birthday = giftDoc.GetValue("Birthday", false).AsBoolean,
                RequirePremium = giftDoc.GetValue("RequirePremium", false).AsBoolean,
                LimitedPerUser = giftDoc.GetValue("LimitedPerUser", false).AsBoolean,
                PeerColorAvailable = giftDoc.GetValue("PeerColorAvailable", false).AsBoolean,
                Auction = giftDoc.GetValue("Auction", false).AsBoolean,
                Sticker = sticker,
                Stars = GetLong(giftDoc, "Stars"),
                ConvertStars = GetLong(giftDoc, "ConvertStars"),
                AvailabilityRemains = GetNullableInt(giftDoc, "AvailabilityRemains"),
                AvailabilityTotal = GetNullableInt(giftDoc, "AvailabilityTotal"),
                AvailabilityResale = GetNullableLong(giftDoc, "AvailabilityResale"),
                FirstSaleDate = GetNullableInt(giftDoc, "FirstSaleDate"),
                LastSaleDate = GetNullableInt(giftDoc, "LastSaleDate"),
                UpgradeStars = GetNullableLong(giftDoc, "UpgradeStars"),
                ResellMinStars = GetNullableLong(giftDoc, "ResellMinStars"),
                Title = GetNullableString(giftDoc, "Title"),
                PerUserTotal = GetNullableInt(giftDoc, "PerUserTotal"),
                PerUserRemains = GetNullableInt(giftDoc, "PerUserRemains"),
                LockedUntilDate = GetNullableInt(giftDoc, "LockedUntilDate"),
                AuctionSlug = GetNullableString(giftDoc, "AuctionSlug"),
                GiftsPerRound = GetNullableInt(giftDoc, "GiftsPerRound"),
                AuctionStartDate = GetNullableInt(giftDoc, "AuctionStartDate"),
                UpgradeVariants = GetNullableInt(giftDoc, "UpgradeVariants"),
                Background = GetStarGiftBackground(giftDoc)
            };

            var savedGift = new TSavedStarGift
            {
                NameHidden = savedGiftDoc.GetValue("NameHidden", false).AsBoolean,
                Unsaved = !savedGiftDoc.GetValue("Saved", false).AsBoolean,
                Refunded = savedGiftDoc.GetValue("Refunded", false).AsBoolean,
                CanUpgrade = savedGiftDoc.GetValue("Upgraded", false).AsBoolean
                    ? craftUpgradableGiftIds.Contains(giftId)
                    : savedGiftDoc.GetValue("CanUpgrade", false).AsBoolean,
                PinnedToTop = savedGiftDoc.GetValue("PinnedToTop", false).AsBoolean,
                UpgradeSeparate = savedGiftDoc.GetValue("UpgradeSeparate", false).AsBoolean,
                Date = savedGiftDoc["Date"].AsInt32,
                Gift = tGift,
                SavedId = GetLong(savedGiftDoc, "SavedId"),
                MsgId = GetNullableInt(savedGiftDoc, "MsgId"),
                ConvertStars = GetNullableLong(savedGiftDoc, "ConvertStars"),
                UpgradeStars = GetNullableLong(savedGiftDoc, "UpgradeStars"),
                TransferStars = GetNullableLong(savedGiftDoc, "TransferStars"),
                CanExportAt = GetNullableInt(savedGiftDoc, "CanExportAt"),
                CanTransferAt = NormalizeCooldown(GetNullableInt(savedGiftDoc, "CanTransferAt")),
                CanResellAt = NormalizeCooldown(GetNullableInt(savedGiftDoc, "CanResellAt")),
                PrepaidUpgradeHash = GetNullableString(savedGiftDoc, "PrepaidUpgradeHash"),
                DropOriginalDetailsStars = GetNullableLong(savedGiftDoc, "DropOriginalDetailsStars"),
                GiftNum = GetNullableInt(savedGiftDoc, "GiftNum")
            };

            // Set FromId if not hidden
            if (savedGiftDoc.Contains("FromUserId") && !savedGiftDoc["FromUserId"].IsBsonNull && !savedGift.NameHidden)
            {
                savedGift.FromId = new TPeerUser { UserId = GetLong(savedGiftDoc, "FromUserId") };
            }

            // Set message if present
            if (savedGiftDoc.Contains("Message") && !savedGiftDoc["Message"].IsBsonNull)
            {
                savedGift.Message = new TTextWithEntities
                {
                    Text = savedGiftDoc["Message"].AsString,
                    Entities = new TVector<IMessageEntity>()
                };
            }

            // Set collection IDs if present
            if (savedGiftDoc.Contains("CollectionId") && !savedGiftDoc["CollectionId"].IsBsonNull && savedGiftDoc["CollectionId"].IsBsonArray)
            {
                var collectionIds = new TVector<int>();
                foreach (var id in savedGiftDoc["CollectionId"].AsBsonArray)
                {
                    collectionIds.Add(id.AsInt32);
                }
                savedGift.CollectionId = collectionIds;
            }

            gifts.Add(savedGift);
        }

        // Get user info via converter service (handles privacy properly)
        var users = new TVector<IUser>();
        if (userIds.Count > 0)
        {
            var userList = await userConverterService.GetUserListAsync(input, userIds.ToList(), true, true, input.Layer);
            foreach (var user in userList)
            {
                users.Add(user);
            }
        }

        return new TSavedStarGifts
        {
            Count = gifts.Count,
            Gifts = gifts,
            Users = users,
            Chats = new TVector<IChat>()
        };
    }

    private static async Task<HashSet<long>> GetCraftUpgradableGiftIdsAsync(
        IMongoCollection<BsonDocument> savedGiftsCollection,
        long ownerUserId,
        IReadOnlyDictionary<long, BsonDocument> giftMap)
    {
        var result = new HashSet<long>();
        foreach (var kv in giftMap)
        {
            var giftId = kv.Key;
            var giftDoc = kv.Value;
            var craftRequiredCount = GetNullableInt(giftDoc, "CraftRequiredCount") ?? 1;
            if (craftRequiredCount <= 1)
            {
                continue;
            }

            var craftFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("OwnerUserId", ownerUserId),
                Builders<BsonDocument>.Filter.Eq("GiftId", giftId),
                Builders<BsonDocument>.Filter.Eq("Upgraded", true),
                Builders<BsonDocument>.Filter.Ne("Converted", true),
                Builders<BsonDocument>.Filter.Ne("Refunded", true)
            );

            var count = await savedGiftsCollection.CountDocumentsAsync(craftFilter);
            if (count >= craftRequiredCount)
            {
                result.Add(giftId);
            }
        }

        return result;
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

    private static int? NormalizeCooldown(int? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return value.Value > now ? now : value.Value;
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
