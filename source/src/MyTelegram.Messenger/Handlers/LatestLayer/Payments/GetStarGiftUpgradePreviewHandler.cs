using MongoDB.Bson;
using MongoDB.Driver;
using System.Collections.Generic;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Obtain a preview of the possible attributes (chosen randomly) a <a href="https://corefork.telegram.org/api/gifts">gift »</a> can receive after upgrading it to a <a href="https://corefork.telegram.org/api/gifts#collectible-gifts">collectible gift »</a>, see <a href="https://corefork.telegram.org/api/gifts#collectible-gifts">here »</a> for more info.
/// Possible errors
/// Code Type Description
/// 400 STARGIFT_INVALID The passed gift is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.getStarGiftUpgradePreview"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetStarGiftUpgradePreviewHandler(
    IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetStarGiftUpgradePreview, MyTelegram.Schema.Payments.IStarGiftUpgradePreview>
{
    protected override async Task<MyTelegram.Schema.Payments.IStarGiftUpgradePreview> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetStarGiftUpgradePreview obj)
    {
        var giftId = obj.GiftId;
        
        // Verify gift exists
        var giftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stargiftreadmodel");
        var giftDoc = await giftsCollection.Find(
            Builders<BsonDocument>.Filter.Eq("GiftId", giftId)
        ).FirstOrDefaultAsync();
        
        if (giftDoc == null)
        {
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
        }

        // Get the gift's sticker as fallback document
        var stickerId = GetLong(giftDoc!, "StickerId");
        var documentsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");
        var giftStickerDoc = await documentsCollection.Find(
            Builders<BsonDocument>.Filter.Eq("DocumentId", stickerId)
        ).FirstOrDefaultAsync();
        
        IDocument fallbackDocument = giftStickerDoc != null 
            ? ConvertDocument(giftStickerDoc) 
            : new TDocumentEmpty { Id = stickerId };
        
        const int SamplePerType = 12; // 12 samples per type (models, patterns, backdrops)
        var sampleAttributes = new TVector<IStarGiftAttribute>();
        
        // Get random model
        var modelsCollection = mongoDatabase.GetCollection<BsonDocument>("stargift_upgrade_models");
        var models = await modelsCollection.Find(
            Builders<BsonDocument>.Filter.Eq("GiftId", giftId)
        ).ToListAsync();
        
        if (models.Count > 0)
        {
            var modelSamples = PickRandom(models, SamplePerType);
            foreach (var randomModel in modelSamples)
            {
                var modelDocId = GetLong(randomModel, "DocumentId");
                IDocument modelDocument = fallbackDocument;
                
                if (modelDocId > 0)
                {
                    var modelStickerDoc = await documentsCollection.Find(
                        Builders<BsonDocument>.Filter.Eq("DocumentId", modelDocId)
                    ).FirstOrDefaultAsync();
                    
                    if (modelStickerDoc != null)
                        modelDocument = ConvertDocument(modelStickerDoc);
                }
                
                sampleAttributes.Add(new TStarGiftAttributeModel
                {
                    Name = GetString(randomModel, "Name") ?? "Model",
                    Document = modelDocument,
                    RarityPermille = GetInt(randomModel, "RarityPermille")
                });
            }
        }
        else
        {
            // No models in DB - add default model using gift's sticker
            for (var i = 0; i < SamplePerType; i++)
            {
                sampleAttributes.Add(new TStarGiftAttributeModel
                {
                    Name = "Original",
                    Document = fallbackDocument,
                    RarityPermille = 1000
                });
            }
        }
        
        // Get random pattern
        var patternsCollection = mongoDatabase.GetCollection<BsonDocument>("stargift_upgrade_patterns");
        var patterns = await patternsCollection.Find(
            Builders<BsonDocument>.Filter.Eq("GiftId", giftId)
        ).ToListAsync();
        
        if (patterns.Count > 0)
        {
            var patternSamples = PickRandom(patterns, SamplePerType);
            foreach (var randomPattern in patternSamples)
            {
                var patternDocId = GetLong(randomPattern, "DocumentId");
                IDocument patternDocument = fallbackDocument;
                
                if (patternDocId > 0)
                {
                    var patternStickerDoc = await documentsCollection.Find(
                        Builders<BsonDocument>.Filter.Eq("DocumentId", patternDocId)
                    ).FirstOrDefaultAsync();
                    
                    if (patternStickerDoc != null)
                        patternDocument = ConvertDocument(patternStickerDoc);
                }
                
                sampleAttributes.Add(new TStarGiftAttributePattern
                {
                    Name = GetString(randomPattern, "Name") ?? "Pattern",
                    Document = patternDocument,
                    RarityPermille = GetInt(randomPattern, "RarityPermille")
                });
            }
        }
        else
        {
            // No patterns in DB - add default pattern using gift's sticker
            for (var i = 0; i < SamplePerType; i++)
            {
                sampleAttributes.Add(new TStarGiftAttributePattern
                {
                    Name = "Classic",
                    Document = fallbackDocument,
                    RarityPermille = 1000
                });
            }
        }
        
        // Get random backdrop
        var backdropsCollection = mongoDatabase.GetCollection<BsonDocument>("stargift_upgrade_backdrops");
        var backdrops = await backdropsCollection.Find(
            Builders<BsonDocument>.Filter.Eq("GiftId", giftId)
        ).ToListAsync();
        
        if (backdrops.Count > 0)
        {
            var backdropSamples = PickRandom(backdrops, SamplePerType);
            foreach (var randomBackdrop in backdropSamples)
            {
                sampleAttributes.Add(new TStarGiftAttributeBackdrop
                {
                    Name = GetString(randomBackdrop, "Name") ?? "Backdrop",
                    BackdropId = GetInt(randomBackdrop, "BackdropId"),
                    CenterColor = GetInt(randomBackdrop, "CenterColor"),
                    EdgeColor = GetInt(randomBackdrop, "EdgeColor"),
                    PatternColor = GetInt(randomBackdrop, "PatternColor"),
                    TextColor = GetInt(randomBackdrop, "TextColor"),
                    RarityPermille = GetInt(randomBackdrop, "RarityPermille")
                });
            }
        }
        else
        {
            // No backdrops in DB - add default backdrop
            for (var i = 0; i < SamplePerType; i++)
            {
                sampleAttributes.Add(new TStarGiftAttributeBackdrop
                {
                    Name = "Default",
                    BackdropId = 1,
                    CenterColor = 0xFFFFFF,  // White
                    EdgeColor = 0x4A90D9,    // Blue
                    PatternColor = 0x2B5278, // Dark blue
                    TextColor = 0x000000,    // Black
                    RarityPermille = 1000
                });
            }
        }
        
        // Get upgrade price from gift
        // UpgradeStars: null = no upgrade available, 0 = free upgrade, > 0 = paid upgrade
        var upgradeStars = GetNullableLong(giftDoc, "UpgradeStars");
        
        // If UpgradeStars is null, upgrade is not available for this gift
        if (!upgradeStars.HasValue)
            RpcErrors.RpcErrors400.StargiftUpgradeUnavailable.ThrowRpcError();
        
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        
        // Prices - current upgrade prices (0 means free upgrade)
        var prices = new TVector<IStarGiftUpgradePrice>
        {
            new TStarGiftUpgradePrice { Date = now, UpgradeStars = upgradeStars!.Value }
        };
        
        // NextPrices - future upgrade prices (can be empty or same as current)
        var nextPrices = new TVector<IStarGiftUpgradePrice>();
        
        return new MyTelegram.Schema.Payments.TStarGiftUpgradePreview
        {
            SampleAttributes = sampleAttributes,
            Prices = prices,
            NextPrices = nextPrices
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
    
    private static long? GetNullableLong(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull) return null;
        return doc[field].IsInt64 ? doc[field].AsInt64 : doc[field].AsInt32;
    }

    private static List<BsonDocument> PickRandom(List<BsonDocument> source, int count)
    {
        var selected = new List<BsonDocument>(count);
        if (source.Count == 0 || count <= 0)
        {
            return selected;
        }

        if (source.Count <= count)
        {
            for (var i = 0; i < count; i++)
            {
                selected.Add(source[Random.Shared.Next(source.Count)]);
            }
            return selected;
        }

        var indices = new int[source.Count];
        for (var i = 0; i < indices.Length; i++)
        {
            indices[i] = i;
        }

        for (var i = 0; i < count; i++)
        {
            var j = Random.Shared.Next(i, indices.Length);
            (indices[i], indices[j]) = (indices[j], indices[i]);
            selected.Add(source[indices[i]]);
        }

        return selected;
    }
}
