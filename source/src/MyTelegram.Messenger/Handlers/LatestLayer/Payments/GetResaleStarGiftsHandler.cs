using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Get <a href="https://corefork.telegram.org/api/gifts#collectible-gifts">collectible gifts</a> of a specific type currently on resale, see <a href="https://corefork.telegram.org/api/gifts#reselling-collectible-gifts">here В»</a> for more info.<code>sort_by_price</code> and <code>sort_by_num</code> are mutually exclusive, if neither are set results are sorted by the unixtime (descending) when their resell price was last changed.See <a href="https://corefork.telegram.org/api/gifts#sending-gifts">here В»</a> for detailed documentation on this method.  
/// Possible errors
/// Code Type Description
/// 400 STARGIFT_INVALID The passed gift is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.getResaleStarGifts"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User вњ”] [Bot вњ–] [Anonymous вњ–]
/// </remarks>
internal sealed class GetResaleStarGiftsHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetResaleStarGifts, MyTelegram.Schema.Payments.IResaleStarGifts>
{
    protected override async Task<MyTelegram.Schema.Payments.IResaleStarGifts> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetResaleStarGifts obj)
    {
        var savedGiftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-savedstargiftreadmodel");
        var giftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stargiftreadmodel");
        var documentsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");

        var baseGiftDoc = await giftsCollection.Find(Builders<BsonDocument>.Filter.Eq("GiftId", obj.GiftId)).FirstOrDefaultAsync();
        if (baseGiftDoc == null)
        {
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
        }

        var listedFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("GiftId", obj.GiftId),
            Builders<BsonDocument>.Filter.Eq("Upgraded", true),
            Builders<BsonDocument>.Filter.Ne("Converted", true),
            Builders<BsonDocument>.Filter.Ne("Refunded", true),
            Builders<BsonDocument>.Filter.Gt(StarGiftResaleHelper.ResaleStarsAmountField, 0)
        );
        var listedDocs = await savedGiftsCollection.Find(listedFilter).ToListAsync();

        if (obj.Attributes?.Count > 0 && listedDocs.Count > 0)
        {
            listedDocs = await ApplyAttributeFilterAsync(mongoDatabase, listedDocs, obj.Attributes);
        }

        SortListedDocs(listedDocs, obj.SortByPrice, obj.SortByNum);

        var totalCount = listedDocs.Count;
        var offset = ParseOffset(obj.Offset);
        var limit = obj.Limit <= 0 ? 20 : Math.Min(obj.Limit, 100);
        var pageDocs = listedDocs.Skip(offset).Take(limit).ToList();
        var nextOffset = offset + pageDocs.Count < totalCount ? (offset + pageDocs.Count).ToString() : null;

        var giftIds = pageDocs.Select(x => GetLong(x, "GiftId")).Distinct().ToList();
        var giftDocs = giftIds.Count == 0
            ? []
            : await giftsCollection.Find(Builders<BsonDocument>.Filter.In("GiftId", giftIds)).ToListAsync();
        var giftMap = giftDocs.ToDictionary(g => GetLong(g, "GiftId"));

        var countersCollection = mongoDatabase.GetCollection<BsonDocument>("stargift_upgrade_counters");
        var counterDocs = giftIds.Count == 0
            ? []
            : await countersCollection.Find(Builders<BsonDocument>.Filter.In("GiftId", giftIds)).ToListAsync();
        var issuedMap = counterDocs.ToDictionary(x => GetLong(x, "GiftId"), x => GetInt(x, "UpgradedCount"));

        var gifts = new TVector<IStarGift>();
        var userIds = new HashSet<long>();
        foreach (var savedGiftDoc in pageDocs)
        {
            var giftId = GetLong(savedGiftDoc, "GiftId");
            if (!giftMap.TryGetValue(giftId, out var giftDoc))
            {
                continue;
            }

            var attributes = await BuildAttributesFromSavedGiftAsync(savedGiftDoc, giftId, documentsCollection);
            var ownerUserId = GetLong(savedGiftDoc, "OwnerUserId");
            userIds.Add(ownerUserId);

            foreach (var attr in attributes)
            {
                if (attr is TStarGiftAttributeOriginalDetails original)
                {
                    if (original.SenderId is TPeerUser senderPeer)
                    {
                        userIds.Add(senderPeer.UserId);
                    }

                    if (original.RecipientId is TPeerUser recipientPeer)
                    {
                        userIds.Add(recipientPeer.UserId);
                    }
                }
            }

            gifts.Add(new TStarGiftUnique
            {
                Id = GetLong(savedGiftDoc, "SavedId"),
                GiftId = giftId,
                Title = GetNullableString(giftDoc, "Title") ?? "Collectible Gift",
                Slug = GetNullableString(savedGiftDoc, "Slug") ?? string.Empty,
                Num = GetNullableInt(savedGiftDoc, "GiftNum") ?? 1,
                OwnerId = new TPeerUser { UserId = ownerUserId },
                Attributes = attributes,
                AvailabilityIssued = issuedMap.TryGetValue(giftId, out var availabilityIssued) ? availabilityIssued : 0,
                AvailabilityTotal = GetNullableInt(giftDoc, "AvailabilityTotal") ?? 0,
                ReleasedBy = GetReleasedByPeer(giftDoc),
                ResellAmount = StarGiftResaleHelper.BuildResellAmount(savedGiftDoc),
                OfferMinStars = ResolveOfferMinStars(savedGiftDoc, giftDoc)
            });
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

        var result = new TResaleStarGifts
        {
            Count = totalCount,
            Gifts = gifts,
            NextOffset = nextOffset,
            Users = users,
            Chats = []
        };

        var distinctAttributes = BuildDistinctAttributes(gifts);
        if (obj.AttributesHash.HasValue)
        {
            var attributesHash = CalculateAttributesHash(distinctAttributes);
            if (obj.AttributesHash.Value != attributesHash)
            {
                result.Attributes = distinctAttributes;
                result.AttributesHash = attributesHash;
            }
        }

        if (string.IsNullOrEmpty(obj.Offset))
        {
            result.Counters = BuildAttributeCounters(gifts);
        }

        return result;
    }

    private static async Task<List<BsonDocument>> ApplyAttributeFilterAsync(
        IMongoDatabase mongoDatabase,
        List<BsonDocument> listedDocs,
        TVector<IStarGiftAttributeId> attributes)
    {
        var modelIds = attributes
            .OfType<TStarGiftAttributeIdModel>()
            .Select(x => x.DocumentId)
            .ToHashSet();
        var patternIds = attributes
            .OfType<TStarGiftAttributeIdPattern>()
            .Select(x => x.DocumentId)
            .ToHashSet();
        var backdropIds = attributes
            .OfType<TStarGiftAttributeIdBackdrop>()
            .Select(x => x.BackdropId)
            .ToHashSet();

        if (modelIds.Count == 0 && patternIds.Count == 0 && backdropIds.Count == 0)
        {
            return listedDocs;
        }

        var modelsCollection = mongoDatabase.GetCollection<BsonDocument>("stargift_upgrade_models");
        var patternsCollection = mongoDatabase.GetCollection<BsonDocument>("stargift_upgrade_patterns");
        var modelDocIdCache = new Dictionary<string, long?>();
        var patternDocIdCache = new Dictionary<string, long?>();

        var filtered = new List<BsonDocument>(listedDocs.Count);
        foreach (var doc in listedDocs)
        {
            var giftId = GetLong(doc, "GiftId");

            if (modelIds.Count > 0)
            {
                var modelName = GetNullableString(doc, "ModelName");
                var modelDocId = await GetAttributeDocumentIdAsync(modelsCollection, modelDocIdCache, giftId, modelName);
                if (!modelDocId.HasValue || !modelIds.Contains(modelDocId.Value))
                {
                    continue;
                }
            }

            if (patternIds.Count > 0)
            {
                var patternName = GetNullableString(doc, "PatternName");
                var patternDocId = await GetAttributeDocumentIdAsync(patternsCollection, patternDocIdCache, giftId, patternName);
                if (!patternDocId.HasValue || !patternIds.Contains(patternDocId.Value))
                {
                    continue;
                }
            }

            if (backdropIds.Count > 0)
            {
                var backdropId = GetNullableInt(doc, "BackdropId");
                if (!backdropId.HasValue || !backdropIds.Contains(backdropId.Value))
                {
                    continue;
                }
            }

            filtered.Add(doc);
        }

        return filtered;
    }

    private static async Task<long?> GetAttributeDocumentIdAsync(
        IMongoCollection<BsonDocument> collection,
        Dictionary<string, long?> cache,
        long giftId,
        string? attributeName)
    {
        if (string.IsNullOrEmpty(attributeName))
        {
            return null;
        }

        var cacheKey = $"{giftId}:{attributeName}";
        if (cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("GiftId", giftId),
            Builders<BsonDocument>.Filter.Eq("Name", attributeName)
        );
        var doc = await collection.Find(filter).FirstOrDefaultAsync();
        var documentId = doc == null ? null : GetNullableLong(doc, "DocumentId");
        cache[cacheKey] = documentId;
        return documentId;
    }

    private static void SortListedDocs(List<BsonDocument> docs, bool sortByPrice, bool sortByNum)
    {
        if (sortByPrice)
        {
            docs.Sort(static (a, b) =>
            {
                var amountA = GetNullableLong(a, StarGiftResaleHelper.ResaleStarsAmountField) ?? long.MaxValue;
                var amountB = GetNullableLong(b, StarGiftResaleHelper.ResaleStarsAmountField) ?? long.MaxValue;
                var compare = amountA.CompareTo(amountB);
                if (compare != 0)
                {
                    return compare;
                }

                var updatedAtA = GetNullableInt(a, StarGiftResaleHelper.ResaleUpdatedAtField) ?? 0;
                var updatedAtB = GetNullableInt(b, StarGiftResaleHelper.ResaleUpdatedAtField) ?? 0;
                return updatedAtB.CompareTo(updatedAtA);
            });
            return;
        }

        if (sortByNum)
        {
            docs.Sort(static (a, b) =>
            {
                var numA = GetNullableInt(a, "GiftNum") ?? int.MaxValue;
                var numB = GetNullableInt(b, "GiftNum") ?? int.MaxValue;
                var compare = numA.CompareTo(numB);
                if (compare != 0)
                {
                    return compare;
                }

                var updatedAtA = GetNullableInt(a, StarGiftResaleHelper.ResaleUpdatedAtField) ?? 0;
                var updatedAtB = GetNullableInt(b, StarGiftResaleHelper.ResaleUpdatedAtField) ?? 0;
                return updatedAtB.CompareTo(updatedAtA);
            });
            return;
        }

        docs.Sort(static (a, b) =>
        {
            var updatedAtA = GetNullableInt(a, StarGiftResaleHelper.ResaleUpdatedAtField) ?? 0;
            var updatedAtB = GetNullableInt(b, StarGiftResaleHelper.ResaleUpdatedAtField) ?? 0;
            var compare = updatedAtB.CompareTo(updatedAtA);
            if (compare != 0)
            {
                return compare;
            }

            var savedIdA = GetLong(a, "SavedId");
            var savedIdB = GetLong(b, "SavedId");
            return savedIdB.CompareTo(savedIdA);
        });
    }

    private async Task<TVector<IStarGiftAttribute>> BuildAttributesFromSavedGiftAsync(
        BsonDocument savedGiftDoc,
        long giftId,
        IMongoCollection<BsonDocument> documentsCollection)
    {
        var attributes = new TVector<IStarGiftAttribute>();

        var modelName = GetNullableString(savedGiftDoc, "ModelName");
        if (!string.IsNullOrEmpty(modelName))
        {
            var modelsCollection = mongoDatabase.GetCollection<BsonDocument>("stargift_upgrade_models");
            var modelDoc = await modelsCollection.Find(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("GiftId", giftId),
                    Builders<BsonDocument>.Filter.Eq("Name", modelName)
                )).FirstOrDefaultAsync();

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

        var patternName = GetNullableString(savedGiftDoc, "PatternName");
        if (!string.IsNullOrEmpty(patternName))
        {
            var patternsCollection = mongoDatabase.GetCollection<BsonDocument>("stargift_upgrade_patterns");
            var patternDoc = await patternsCollection.Find(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("GiftId", giftId),
                    Builders<BsonDocument>.Filter.Eq("Name", patternName)
                )).FirstOrDefaultAsync();

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

        var backdropName = GetNullableString(savedGiftDoc, "BackdropName");
        if (!string.IsNullOrEmpty(backdropName))
        {
            var backdropRarity = 0;
            var backdropsCollection = mongoDatabase.GetCollection<BsonDocument>("stargift_upgrade_backdrops");
            var backdropDoc = await backdropsCollection.Find(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("GiftId", giftId),
                    Builders<BsonDocument>.Filter.Eq("Name", backdropName)
                )).FirstOrDefaultAsync();
            if (backdropDoc != null)
            {
                backdropRarity = GetInt(backdropDoc, "RarityPermille");
            }

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
            {
                originalDetails.SenderId = new TPeerUser { UserId = fromUserId.Value };
            }

            if (!string.IsNullOrEmpty(message))
            {
                originalDetails.Message = new TTextWithEntities { Text = message, Entities = [] };
            }

            attributes.Add(originalDetails);
        }

        return attributes;
    }

    private static TVector<IStarGiftAttribute> BuildDistinctAttributes(TVector<IStarGift> gifts)
    {
        var uniqueByKey = new Dictionary<string, IStarGiftAttribute>();
        foreach (var gift in gifts.OfType<TStarGiftUnique>())
        {
            foreach (var attribute in gift.Attributes)
            {
                var key = GetAttributeKey(attribute);
                if (key == null || uniqueByKey.ContainsKey(key))
                {
                    continue;
                }

                uniqueByKey[key] = attribute;
            }
        }

        var result = new TVector<IStarGiftAttribute>();
        foreach (var attribute in uniqueByKey.Values)
        {
            result.Add(attribute);
        }

        return result;
    }

    private static TVector<IStarGiftAttributeCounter> BuildAttributeCounters(TVector<IStarGift> gifts)
    {
        var map = new Dictionary<string, (IStarGiftAttributeId AttributeId, int Count)>();
        foreach (var gift in gifts.OfType<TStarGiftUnique>())
        {
            foreach (var attribute in gift.Attributes)
            {
                var attributeId = ToAttributeId(attribute);
                var key = GetAttributeKey(attribute);
                if (attributeId == null || key == null)
                {
                    continue;
                }

                if (map.TryGetValue(key, out var item))
                {
                    map[key] = (item.AttributeId, item.Count + 1);
                }
                else
                {
                    map[key] = (attributeId, 1);
                }
            }
        }

        var result = new TVector<IStarGiftAttributeCounter>();
        foreach (var (_, value) in map)
        {
            result.Add(new TStarGiftAttributeCounter
            {
                Attribute = value.AttributeId,
                Count = value.Count
            });
        }

        return result;
    }

    private static long CalculateAttributesHash(TVector<IStarGiftAttribute> attributes)
    {
        unchecked
        {
            long hash = 17;
            var keys = attributes
                .Select(GetAttributeKey)
                .Where(x => x != null)
                .OrderBy(x => x)
                .ToList();

            foreach (var key in keys)
            {
                hash = hash * 31 + key!.GetHashCode();
            }

            return hash;
        }
    }

    private static IStarGiftAttributeId? ToAttributeId(IStarGiftAttribute attribute)
    {
        return attribute switch
        {
            TStarGiftAttributeModel model => new TStarGiftAttributeIdModel { DocumentId = model.Document.Id },
            TStarGiftAttributePattern pattern => new TStarGiftAttributeIdPattern { DocumentId = pattern.Document.Id },
            TStarGiftAttributeBackdrop backdrop => new TStarGiftAttributeIdBackdrop { BackdropId = backdrop.BackdropId },
            _ => null
        };
    }

    private static string? GetAttributeKey(IStarGiftAttribute attribute)
    {
        return attribute switch
        {
            TStarGiftAttributeModel model => $"m:{model.Document.Id}",
            TStarGiftAttributePattern pattern => $"p:{pattern.Document.Id}",
            TStarGiftAttributeBackdrop backdrop => $"b:{backdrop.BackdropId}",
            _ => null
        };
    }

    private static int ParseOffset(string offset)
    {
        if (string.IsNullOrEmpty(offset))
        {
            return 0;
        }

        return int.TryParse(offset, out var parsed) && parsed > 0 ? parsed : 0;
    }

    private static IPeer? GetReleasedByPeer(BsonDocument doc)
    {
        var peerId = GetNullableLong(doc, "ReleasedByPeerId");
        var peerType = GetNullableInt(doc, "ReleasedByPeerType");
        if (!peerId.HasValue || !peerType.HasValue)
        {
            return null;
        }

        return peerType.Value switch
        {
            1 => new TPeerUser { UserId = peerId.Value },
            2 => new TPeerChat { ChatId = peerId.Value },
            3 => new TPeerChannel { ChannelId = peerId.Value },
            _ => null
        };
    }

    private static IDocument ConvertDocument(BsonDocument doc) => new TDocument
    {
        Id = GetLong(doc, "DocumentId"),
        AccessHash = GetLong(doc, "AccessHash"),
        Date = doc["Date"].AsInt32,
        MimeType = doc["MimeType"].AsString,
        Size = GetLong(doc, "Size"),
        DcId = doc["DcId"].AsInt32,
        FileReference = doc.Contains("FileReference") && !doc["FileReference"].IsBsonNull ? doc["FileReference"].AsByteArray : [],
        Attributes = []
    };

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
}
