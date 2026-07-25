
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.Payments;

/// <summary>
/// <para><c>See <a href="" /> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ] [Bot ] [Anonymous ]
/// </remarks>
internal sealed class GetStarGiftUpgradeAttributesHandler(
    IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetStarGiftUpgradeAttributes, MyTelegram.Schema.Payments.IStarGiftUpgradeAttributes>, IObjectHandler
{
    protected override async Task<MyTelegram.Schema.Payments.IStarGiftUpgradeAttributes> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetStarGiftUpgradeAttributes obj)
    {
        var giftId = obj.GiftId;
        var giftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stargiftreadmodel");
        var documentsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");

        var giftDoc = await giftsCollection.Find(
            Builders<BsonDocument>.Filter.Eq("GiftId", giftId)
        ).FirstOrDefaultAsync();

        if (giftDoc == null)
        {
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
        }

        // UpgradeStars: null means upgrade isn't available for this gift.
        if (!giftDoc!.Contains("UpgradeStars") || giftDoc["UpgradeStars"].IsBsonNull)
        {
            RpcErrors.RpcErrors400.StargiftUpgradeUnavailable.ThrowRpcError();
        }

        var stickerId = GetLong(giftDoc, "StickerId");
        var giftStickerDoc = await documentsCollection.Find(
            Builders<BsonDocument>.Filter.Eq("DocumentId", stickerId)
        ).FirstOrDefaultAsync();
        IDocument fallbackDocument = giftStickerDoc != null
            ? ConvertDocument(giftStickerDoc)
            : new TDocumentEmpty { Id = stickerId };

        var attributes = new TVector<IStarGiftAttribute>();

        var modelsCollection = mongoDatabase.GetCollection<BsonDocument>("stargift_upgrade_models");
        var modelDocs = await modelsCollection.Find(
            Builders<BsonDocument>.Filter.Eq("GiftId", giftId)
        ).ToListAsync();

        foreach (var model in modelDocs)
        {
            var modelDocId = GetLong(model, "DocumentId");
            var modelStickerDoc = modelDocId > 0
                ? await documentsCollection.Find(Builders<BsonDocument>.Filter.Eq("DocumentId", modelDocId)).FirstOrDefaultAsync()
                : null;

            attributes.Add(new TStarGiftAttributeModel
            {
                Name = GetString(model, "Name") ?? "Model",
                Document = modelStickerDoc != null ? ConvertDocument(modelStickerDoc) : fallbackDocument,
                RarityPermille = GetInt(model, "RarityPermille")
            });
        }

        var patternsCollection = mongoDatabase.GetCollection<BsonDocument>("stargift_upgrade_patterns");
        var patternDocs = await patternsCollection.Find(
            Builders<BsonDocument>.Filter.Eq("GiftId", giftId)
        ).ToListAsync();

        foreach (var pattern in patternDocs)
        {
            var patternDocId = GetLong(pattern, "DocumentId");
            var patternStickerDoc = patternDocId > 0
                ? await documentsCollection.Find(Builders<BsonDocument>.Filter.Eq("DocumentId", patternDocId)).FirstOrDefaultAsync()
                : null;

            attributes.Add(new TStarGiftAttributePattern
            {
                Name = GetString(pattern, "Name") ?? "Pattern",
                Document = patternStickerDoc != null ? ConvertDocument(patternStickerDoc) : fallbackDocument,
                RarityPermille = GetInt(pattern, "RarityPermille")
            });
        }

        var backdropsCollection = mongoDatabase.GetCollection<BsonDocument>("stargift_upgrade_backdrops");
        var backdropDocs = await backdropsCollection.Find(
            Builders<BsonDocument>.Filter.Eq("GiftId", giftId)
        ).ToListAsync();

        foreach (var backdrop in backdropDocs)
        {
            attributes.Add(new TStarGiftAttributeBackdrop
            {
                Name = GetString(backdrop, "Name") ?? "Backdrop",
                BackdropId = GetInt(backdrop, "BackdropId"),
                CenterColor = GetInt(backdrop, "CenterColor"),
                EdgeColor = GetInt(backdrop, "EdgeColor"),
                PatternColor = GetInt(backdrop, "PatternColor"),
                TextColor = GetInt(backdrop, "TextColor"),
                RarityPermille = GetInt(backdrop, "RarityPermille")
            });
        }

        return new MyTelegram.Schema.Payments.TStarGiftUpgradeAttributes
        {
            Attributes = attributes
        };
    }

    private static IDocument ConvertDocument(BsonDocument doc)
    {
        return new TDocument
        {
            Id = GetLong(doc, "DocumentId"),
            AccessHash = GetLong(doc, "AccessHash"),
            Date = GetInt(doc, "Date"),
            MimeType = GetString(doc, "MimeType") ?? "application/x-tgsticker",
            Size = GetLong(doc, "Size"),
            DcId = GetInt(doc, "DcId"),
            FileReference = doc.Contains("FileReference") && !doc["FileReference"].IsBsonNull
                ? doc["FileReference"].AsByteArray
                : [],
            Attributes = []
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
        var value = doc[field];
        return value.IsInt32 ? value.AsInt32 : (int)value.AsInt64;
    }

    private static string? GetString(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull) return null;
        return doc[field].AsString;
    }
}

