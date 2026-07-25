using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Fetch the full list of <a href="https://corefork.telegram.org/api/gifts">gifts</a> owned by a peer.Note that unlike what the name suggests, the method can be used to fetch both "saved" and "unsaved" gifts (aka gifts both pinned and not pinned) to the profile, depending on the passed flags.
/// Possible errors
/// Code Type Description
/// 400 BUSINESS_CONNECTION_INVALID The <code>connection_id</code> passed to the wrapping <a href="https://corefork.telegram.org/api/business">invokeWithBusinessConnection</a> call is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.getSavedStarGifts"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetSavedStarGiftsHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IUserConverterService userConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetSavedStarGifts, MyTelegram.Schema.Payments.ISavedStarGifts>
{
    protected override async Task<MyTelegram.Schema.Payments.ISavedStarGifts> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetSavedStarGifts obj)
    {
        // Get the peer whose gifts we want to fetch
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        var ownerUserId = peer.PeerId;

        // Get saved gifts from MongoDB
        var savedGiftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-savedstargiftreadmodel");
        var filter = Builders<BsonDocument>.Filter.Eq("OwnerUserId", ownerUserId);

        // If requesting user is NOT the owner, only show saved (visible) gifts
        var isOwner = input.UserId == ownerUserId;
        if (!isOwner)
        {
            filter &= Builders<BsonDocument>.Filter.Eq("Saved", true);
        }

        // Apply filters (only relevant when viewing own gifts)
        if (obj.ExcludeSaved)
        {
            filter &= Builders<BsonDocument>.Filter.Ne("Saved", true);
        }
        if (obj.ExcludeUnsaved)
        {
            filter &= Builders<BsonDocument>.Filter.Eq("Saved", true);
        }
        if (obj.ExcludeUnique)
        {
            filter &= Builders<BsonDocument>.Filter.Ne("Upgraded", true);
        }
        if (obj.CollectionId.HasValue)
        {
            filter &= Builders<BsonDocument>.Filter.AnyEq("CollectionId", obj.CollectionId.Value);
        }

        var baseSort = obj.SortByValue
            ? Builders<BsonDocument>.Sort.Descending("ConvertStars")
            : Builders<BsonDocument>.Sort.Descending("Date");
        var sort = Builders<BsonDocument>.Sort.Combine(
            Builders<BsonDocument>.Sort.Descending("PinnedToTop"),
            Builders<BsonDocument>.Sort.Ascending("PinnedOrder"),
            baseSort,
            Builders<BsonDocument>.Sort.Descending("Date")
        );

        var totalCountLong = await savedGiftsCollection.CountDocumentsAsync(filter);
        var totalCount = totalCountLong > int.MaxValue ? int.MaxValue : (int)totalCountLong;

        var offset = 0;
        if (!string.IsNullOrWhiteSpace(obj.Offset) && int.TryParse(obj.Offset, out var parsedOffset) && parsedOffset > 0)
        {
            offset = parsedOffset;
        }

        var query = savedGiftsCollection.Find(filter).Sort(sort);
        if (offset > 0)
        {
            query = query.Skip(offset);
        }
        if (obj.Limit > 0)
        {
            query = query.Limit(obj.Limit);
        }

        var savedGiftDocs = await query.ToListAsync();

        if (savedGiftDocs.Count == 0)
        {
            return new TSavedStarGifts { Count = totalCount, Chats = [], Gifts = [], Users = [] };
        }

        // Get gift definitions
        var giftIds = savedGiftDocs.Select(g => GetLong(g, "GiftId")).Distinct().ToList();
        var giftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stargiftreadmodel");
        var giftFilter = Builders<BsonDocument>.Filter.In("GiftId", giftIds);
        var giftDocs = await giftsCollection.Find(giftFilter).ToListAsync();
        var giftMap = giftDocs.ToDictionary(g => GetLong(g, "GiftId"));

        // Get stickers
        var stickerIds = giftDocs.Select(g => GetLong(g, "StickerId")).Distinct().ToList();
        var documentsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");
        var docFilter = Builders<BsonDocument>.Filter.In("DocumentId", stickerIds);
        var stickerDocs = await documentsCollection.Find(docFilter).ToListAsync();
        var stickerMap = stickerDocs.ToDictionary(d => GetLong(d, "DocumentId"));

        // Get upgrade counters for upgraded gifts
        var upgradedGiftIds = savedGiftDocs
            .Where(g => g.GetValue("Upgraded", false).AsBoolean)
            .Select(g => GetLong(g, "GiftId"))
            .Distinct()
            .ToList();
        
        var upgradeCounterMap = new Dictionary<long, int>();
        if (upgradedGiftIds.Count > 0)
        {
            var countersCollection = mongoDatabase.GetCollection<BsonDocument>("stargift_upgrade_counters");
            var counterFilter = Builders<BsonDocument>.Filter.In("GiftId", upgradedGiftIds);
            var counterDocs = await countersCollection.Find(counterFilter).ToListAsync();
            foreach (var counterDoc in counterDocs)
            {
                upgradeCounterMap[GetLong(counterDoc, "GiftId")] = GetInt(counterDoc, "UpgradedCount");
            }
        }

        // Collect user IDs
        var userIds = new HashSet<long> { ownerUserId };
        foreach (var savedGift in savedGiftDocs)
        {
            if (savedGift.Contains("FromUserId") && !savedGift["FromUserId"].IsBsonNull)
            {
                userIds.Add(GetLong(savedGift, "FromUserId"));
            }
        }

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
            IDocument sticker;
            if (stickerMap.TryGetValue(stickerId, out var stickerDoc))
            {
                sticker = ConvertDocument(stickerDoc);
            }
            else
            {
                sticker = new TDocumentEmpty { Id = stickerId };
            }

            var isUpgraded = savedGiftDoc.GetValue("Upgraded", false).AsBoolean;
            IStarGift gift;

            if (isUpgraded)
            {
                // Build TStarGiftUnique for upgraded gifts
                var attributes = await BuildAttributesFromSavedGiftAsync(savedGiftDoc, giftId, documentsCollection);
                
                // Get total upgraded count from counter collection
                var availabilityIssued = upgradeCounterMap.TryGetValue(giftId, out var count) ? count : 0;
                var availabilityTotal = GetNullableInt(giftDoc, "AvailabilityTotal") ?? 0;

                gift = new TStarGiftUnique
                {
                    Id = GetLong(savedGiftDoc, "SavedId"),
                    GiftId = giftId,
                    Title = GetNullableString(giftDoc, "Title") ?? "Collectible Gift",
                    Slug = GetNullableString(savedGiftDoc, "Slug") ?? "",
                    Num = GetNullableInt(savedGiftDoc, "GiftNum") ?? 1,
                    OwnerId = new TPeerUser { UserId = ownerUserId },
                    Attributes = attributes,
                    AvailabilityIssued = availabilityIssued,
                    AvailabilityTotal = availabilityTotal,
                    ResellAmount = StarGiftResaleHelper.BuildResellAmount(savedGiftDoc),
                    OfferMinStars = ResolveOfferMinStars(savedGiftDoc, giftDoc)
                };
            }
            else
            {
                // Build TStarGift for regular gifts
                gift = new TStarGift
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
                    AuctionSlug = GetNullableString(giftDoc, "AuctionSlug"),
                    GiftsPerRound = GetNullableInt(giftDoc, "GiftsPerRound"),
                    AuctionStartDate = GetNullableInt(giftDoc, "AuctionStartDate"),
                    UpgradeVariants = GetNullableInt(giftDoc, "UpgradeVariants"),
                    Background = GetStarGiftBackground(giftDoc)
                };
            }

            var savedGift = new TSavedStarGift
            {
                NameHidden = savedGiftDoc.GetValue("NameHidden", false).AsBoolean,
                Unsaved = !savedGiftDoc.GetValue("Saved", false).AsBoolean,
                Refunded = savedGiftDoc.GetValue("Refunded", false).AsBoolean,
                CanUpgrade = savedGiftDoc.GetValue("CanUpgrade", false).AsBoolean,
                PinnedToTop = savedGiftDoc.GetValue("PinnedToTop", false).AsBoolean,
                UpgradeSeparate = savedGiftDoc.GetValue("UpgradeSeparate", false).AsBoolean,
                Date = savedGiftDoc["Date"].AsInt32,
                Gift = gift,
                MsgId = GetNullableInt(savedGiftDoc, "MsgId"),
                SavedId = GetNullableLong(savedGiftDoc, "SavedId"),
                ConvertStars = isUpgraded ? null : GetNullableLong(savedGiftDoc, "ConvertStars"),
                UpgradeStars = isUpgraded ? null : GetNullableLong(savedGiftDoc, "UpgradeStars"),
                CanExportAt = GetNullableInt(savedGiftDoc, "CanExportAt"),
                TransferStars = GetNullableLong(savedGiftDoc, "TransferStars"),
                CanTransferAt = NormalizeCooldown(GetNullableInt(savedGiftDoc, "CanTransferAt")),
                CanResellAt = NormalizeCooldown(GetNullableInt(savedGiftDoc, "CanResellAt")),
                PrepaidUpgradeHash = isUpgraded ? null : GetNullableString(savedGiftDoc, "PrepaidUpgradeHash"),
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

        var nextOffset = (obj.Limit > 0 && (long)offset + gifts.Count < totalCountLong)
            ? (offset + gifts.Count).ToString()
            : null;

        return new TSavedStarGifts
        {
            Count = totalCount,
            NextOffset = nextOffset,
            Gifts = gifts,
            Users = users,
            Chats = new TVector<IChat>()
        };
    }

    private async Task<TVector<IStarGiftAttribute>> BuildAttributesFromSavedGiftAsync(
        BsonDocument savedGiftDoc, long giftId, IMongoCollection<BsonDocument> documentsCollection)
    {
        var attributes = new TVector<IStarGiftAttribute>();

        // Get model attribute
        var modelName = GetNullableString(savedGiftDoc, "ModelName");
        if (!string.IsNullOrEmpty(modelName))
        {
            var modelsCollection = mongoDatabase.GetCollection<BsonDocument>("stargift_upgrade_models");
            var modelDoc = await modelsCollection.Find(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("GiftId", giftId),
                    Builders<BsonDocument>.Filter.Eq("Name", modelName)
                )
            ).FirstOrDefaultAsync();

            if (modelDoc != null)
            {
                var modelDocId = GetLong(modelDoc, "DocumentId");
                var modelStickerDoc = await documentsCollection.Find(
                    Builders<BsonDocument>.Filter.Eq("DocumentId", modelDocId)
                ).FirstOrDefaultAsync();

                attributes.Add(new TStarGiftAttributeModel
                {
                    Name = modelName,
                    Document = modelStickerDoc != null ? ConvertDocument(modelStickerDoc) : new TDocumentEmpty { Id = modelDocId },
                    RarityPermille = GetInt(modelDoc, "RarityPermille")
                });
            }
        }

        // Get pattern attribute
        var patternName = GetNullableString(savedGiftDoc, "PatternName");
        if (!string.IsNullOrEmpty(patternName))
        {
            var patternsCollection = mongoDatabase.GetCollection<BsonDocument>("stargift_upgrade_patterns");
            var patternDoc = await patternsCollection.Find(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("GiftId", giftId),
                    Builders<BsonDocument>.Filter.Eq("Name", patternName)
                )
            ).FirstOrDefaultAsync();

            if (patternDoc != null)
            {
                var patternDocId = GetLong(patternDoc, "DocumentId");
                var patternStickerDoc = await documentsCollection.Find(
                    Builders<BsonDocument>.Filter.Eq("DocumentId", patternDocId)
                ).FirstOrDefaultAsync();

                attributes.Add(new TStarGiftAttributePattern
                {
                    Name = patternName,
                    Document = patternStickerDoc != null ? ConvertDocument(patternStickerDoc) : new TDocumentEmpty { Id = patternDocId },
                    RarityPermille = GetInt(patternDoc, "RarityPermille")
                });
            }
        }

        // Get backdrop attribute from saved gift
        var backdropName = GetNullableString(savedGiftDoc, "BackdropName");
        if (!string.IsNullOrEmpty(backdropName))
        {
            var backdropRarity = 0;
            var backdropsCollection = mongoDatabase.GetCollection<BsonDocument>("stargift_upgrade_backdrops");
            var backdropDoc = await backdropsCollection.Find(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("GiftId", giftId),
                    Builders<BsonDocument>.Filter.Eq("Name", backdropName)
                )
            ).FirstOrDefaultAsync();
            if (backdropDoc != null)
                backdropRarity = GetInt(backdropDoc, "RarityPermille");

            attributes.Add(new TStarGiftAttributeBackdrop
            {
                Name = backdropName,
                BackdropId = GetInt(savedGiftDoc, "BackdropId"),
                CenterColor = GetInt(savedGiftDoc, "BackdropCenterColor"),
                EdgeColor = GetInt(savedGiftDoc, "BackdropEdgeColor"),
                PatternColor = GetInt(savedGiftDoc, "BackdropPatternColor"),
                TextColor = GetInt(savedGiftDoc, "BackdropTextColor"),
                RarityPermille = backdropRarity
            });
        }

        // Add original details if kept
        if (savedGiftDoc.GetValue("KeepOriginalDetails", false).AsBoolean)
        {
            var fromUserId = GetNullableLong(savedGiftDoc, "FromUserId");
            var giftDate = GetInt(savedGiftDoc, "Date");
            var message = GetNullableString(savedGiftDoc, "Message");
            var ownerUserId = GetLong(savedGiftDoc, "OwnerUserId");

            var originalDetails = new TStarGiftAttributeOriginalDetails
            {
                RecipientId = new TPeerUser { UserId = ownerUserId },
                Date = giftDate
            };
            if (fromUserId.HasValue)
                originalDetails.SenderId = new TPeerUser { UserId = fromUserId.Value };
            if (!string.IsNullOrEmpty(message))
                originalDetails.Message = new TTextWithEntities { Text = message, Entities = [] };
            attributes.Add(originalDetails);
        }

        return attributes;
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

    private static int GetInt(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull) return 0;
        return doc[field].IsInt32 ? doc[field].AsInt32 : (int)doc[field].AsInt64;
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

    private static int? ResolveOfferMinStars(BsonDocument savedGiftDoc, BsonDocument giftDoc)
    {
        var minOffer = GetNullableLong(savedGiftDoc, "OfferMinStars")
            ?? GetNullableLong(giftDoc, "ResellMinStars")
            ?? GetNullableLong(giftDoc, "Stars")
            ?? 1;
        if (minOffer <= 0)
        {
            minOffer = 1;
        }

        return minOffer > int.MaxValue ? int.MaxValue : (int)minOffer;
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
