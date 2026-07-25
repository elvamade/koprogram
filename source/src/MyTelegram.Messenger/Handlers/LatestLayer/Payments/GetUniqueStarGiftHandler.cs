using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class GetUniqueStarGiftHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetUniqueStarGift, MyTelegram.Schema.Payments.IUniqueStarGift>
{
    protected override async Task<MyTelegram.Schema.Payments.IUniqueStarGift> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetUniqueStarGift obj)
    {
        var savedGiftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-savedstargiftreadmodel");
        var giftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stargiftreadmodel");
        var documentsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");

        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("Slug", obj.Slug),
            Builders<BsonDocument>.Filter.Eq("Upgraded", true)
        );
        var savedGiftDoc = await savedGiftsCollection.Find(filter).FirstOrDefaultAsync();

        if (savedGiftDoc == null)
            RpcErrors.RpcErrors400.StargiftSlugInvalid.ThrowRpcError();

        var giftId = GetLong(savedGiftDoc!, "GiftId");
        var giftDoc = await giftsCollection.Find(
            Builders<BsonDocument>.Filter.Eq("GiftId", giftId)
        ).FirstOrDefaultAsync();

        if (giftDoc == null)
            RpcErrors.RpcErrors400.StargiftNotFound.ThrowRpcError();

        // Build attributes from saved gift data
        var attributes = await BuildAttributesFromSavedGiftAsync(savedGiftDoc, giftId, documentsCollection);

        var ownerUserId = GetLong(savedGiftDoc, "OwnerUserId");
        var giftNum = GetNullableInt(savedGiftDoc, "GiftNum") ?? 1;
        var title = GetNullableString(giftDoc!, "Title") ?? "Collectible Gift";
        var slug = GetNullableString(savedGiftDoc, "Slug") ?? obj.Slug;
        var availabilityTotal = GetNullableInt(giftDoc, "AvailabilityTotal") ?? 0;
        var availabilityIssued = await GetIssuedCountAsync(giftId);

        var uniqueGift = new TStarGiftUnique
        {
            Id = GetLong(savedGiftDoc, "SavedId"),
            GiftId = giftId,
            Title = title,
            Slug = slug,
            Num = giftNum,
            OwnerId = new TPeerUser { UserId = ownerUserId },
            Attributes = attributes,
            AvailabilityIssued = availabilityIssued,
            AvailabilityTotal = availabilityTotal,
            ResellAmount = StarGiftResaleHelper.BuildResellAmount(savedGiftDoc),
            OfferMinStars = ResolveOfferMinStars(savedGiftDoc, giftDoc)
        };

        var userIds = new List<long> { ownerUserId };
        var userList = await userConverterService.GetUserListAsync(input, userIds, true, true, input.Layer);
        var users = new TVector<IUser>();
        foreach (var user in userList) users.Add(user);

        return new TUniqueStarGift { Gift = uniqueGift, Users = users, Chats = [] };
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

    private async Task<int> GetIssuedCountAsync(long giftId)
    {
        var countersCollection = mongoDatabase.GetCollection<BsonDocument>("stargift_upgrade_counters");
        var counterDoc = await countersCollection.Find(
            Builders<BsonDocument>.Filter.Eq("GiftId", giftId)
        ).FirstOrDefaultAsync();
        
        return counterDoc != null ? GetInt(counterDoc, "UpgradedCount") : 0;
    }

    private static IDocument ConvertDocument(BsonDocument doc) => new TDocument
    {
        Id = GetLong(doc, "DocumentId"), AccessHash = GetLong(doc, "AccessHash"),
        Date = doc["Date"].AsInt32, MimeType = doc["MimeType"].AsString,
        Size = GetLong(doc, "Size"), DcId = doc["DcId"].AsInt32,
        FileReference = doc.Contains("FileReference") && !doc["FileReference"].IsBsonNull ? doc["FileReference"].AsByteArray : [],
        Attributes = []
    };

    private static long GetLong(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull) return 0;
        return doc[field].IsInt64 ? doc[field].AsInt64 : doc[field].AsInt32;
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
