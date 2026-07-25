using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Handlers.LatestLayer.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Get webpage info
/// Possible errors
/// Code Type Description
/// 400 WC_CONVERT_URL_INVALID WC convert URL invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getWebPage"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetWebPageHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetWebPage, MyTelegram.Schema.Messages.IWebPage>
{
    protected override async Task<MyTelegram.Schema.Messages.IWebPage> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Messages.RequestGetWebPage obj)
    {
        var url = obj.Url;
        
        if (string.IsNullOrWhiteSpace(url))
        {
            return new MyTelegram.Schema.Messages.TWebPage { Webpage = new TWebPageEmpty(), Users = [], Chats = [] };
        }

        // Handle NFT/collectible gift links
        var nftResult = await TryGetNftWebPageAsync(input, url);
        if (nftResult != null)
        {
            return nftResult;
        }

        // Handle gift code links
        var giftCodeResult = TryGetGiftCodeWebPage(url);
        if (giftCodeResult != null)
        {
            return giftCodeResult;
        }

        // Default: empty
        return new MyTelegram.Schema.Messages.TWebPage { Webpage = new TWebPageEmpty(), Users = [], Chats = [] };
    }

    private async Task<MyTelegram.Schema.Messages.TWebPage?> TryGetNftWebPageAsync(IRequestInput input, string url)
    {
        string? slug = null;

        // Parse t.me/nft/<slug>
        var tmeMatch = Regex.Match(url, @"t\.me/nft/([a-zA-Z0-9_-]+)", RegexOptions.IgnoreCase);
        if (tmeMatch.Success)
        {
            slug = tmeMatch.Groups[1].Value;
        }

        // Parse tg://nft?slug=<slug>
        if (slug == null)
        {
            var tgMatch = Regex.Match(url, @"tg://nft\?slug=([a-zA-Z0-9_-]+)", RegexOptions.IgnoreCase);
            if (tgMatch.Success)
            {
                slug = tgMatch.Groups[1].Value;
            }
        }

        if (string.IsNullOrEmpty(slug))
        {
            return null;
        }

        var savedGiftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-savedstargiftreadmodel");
        var giftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stargiftreadmodel");
        var documentsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");

        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("Slug", slug),
            Builders<BsonDocument>.Filter.Eq("Upgraded", true)
        );
        var savedGiftDoc = await savedGiftsCollection.Find(filter).FirstOrDefaultAsync();

        if (savedGiftDoc == null)
        {
            return new MyTelegram.Schema.Messages.TWebPage
            {
                Webpage = new MyTelegram.Schema.TWebPage
                {
                    Id = Random.Shared.NextInt64(),
                    Url = url,
                    DisplayUrl = $"t.me/nft/{slug}",
                    Type = "telegram_nft",
                    SiteName = "Telegram",
                    Title = "Collectible Gift",
                    Description = "This collectible gift was not found or is no longer available."
                },
                Users = [],
                Chats = []
            };
        }

        var giftId = GetLong(savedGiftDoc, "GiftId");
        var giftDoc = await giftsCollection.Find(
            Builders<BsonDocument>.Filter.Eq("GiftId", giftId)
        ).FirstOrDefaultAsync();

        var title = GetNullableString(giftDoc, "Title") ?? "Collectible Gift";
        var giftNum = GetNullableInt(savedGiftDoc, "GiftNum") ?? 1;
        var availabilityTotal = GetNullableInt(giftDoc, "AvailabilityTotal") ?? 0;
        var ownerUserId = GetLong(savedGiftDoc, "OwnerUserId");

        var attributes = await BuildAttributesFromSavedGiftAsync(savedGiftDoc, giftId, documentsCollection);
        var availabilityIssued = await GetIssuedCountAsync(giftId);

        var uniqueGift = new TStarGiftUnique
        {
            Id = GetLong(savedGiftDoc, "SavedId"),
            GiftId = giftId,
            Title = title,
            Slug = slug,
            Num = giftNum,
            OwnerId = ownerUserId > 0 ? new TPeerUser { UserId = ownerUserId } : null,
            Attributes = attributes,
            AvailabilityIssued = availabilityIssued,
            AvailabilityTotal = availabilityTotal,
            ResellAmount = StarGiftResaleHelper.BuildResellAmount(savedGiftDoc),
            OfferMinStars = ResolveOfferMinStars(savedGiftDoc, giftDoc)
        };

        var displayTitle = $"{title} #{giftNum}";
        if (availabilityTotal > 0)
        {
            displayTitle += $" of {availabilityTotal}";
        }

        var users = new TVector<IUser>();
        if (ownerUserId > 0)
        {
            var userList = await userConverterService.GetUserListAsync(input, [ownerUserId], true, true, input.Layer);
            foreach (var user in userList) users.Add(user);
        }

        var webPageAttribute = new TWebPageAttributeUniqueStarGift { Gift = uniqueGift };

        return new MyTelegram.Schema.Messages.TWebPage
        {
            Webpage = new MyTelegram.Schema.TWebPage
            {
                Id = Random.Shared.NextInt64(),
                Url = url,
                DisplayUrl = $"t.me/nft/{slug}",
                Type = "telegram_nft",
                SiteName = "Telegram",
                Title = displayTitle,
                Description = "Unique collectible gift on Telegram",
                Attributes = [webPageAttribute]
            },
            Users = users,
            Chats = []
        };
    }

    private async Task<TVector<IStarGiftAttribute>> BuildAttributesFromSavedGiftAsync(
        BsonDocument savedGiftDoc, long giftId, IMongoCollection<BsonDocument> documentsCollection)
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

    private static MyTelegram.Schema.Messages.TWebPage? TryGetGiftCodeWebPage(string url)
    {
        string? slug = null;

        var tmeMatch = Regex.Match(url, @"t\.me/giftcode/([a-zA-Z0-9_-]+)", RegexOptions.IgnoreCase);
        if (tmeMatch.Success)
            slug = tmeMatch.Groups[1].Value;

        if (slug == null)
        {
            var tgMatch = Regex.Match(url, @"tg://giftcode\?slug=([a-zA-Z0-9_-]+)", RegexOptions.IgnoreCase);
            if (tgMatch.Success)
                slug = tgMatch.Groups[1].Value;
        }

        if (string.IsNullOrEmpty(slug))
            return null;

        return new MyTelegram.Schema.Messages.TWebPage
        {
            Webpage = new MyTelegram.Schema.TWebPage
            {
                Id = Random.Shared.NextInt64(),
                Url = url,
                DisplayUrl = $"t.me/giftcode/{slug}",
                Type = "telegram_giftcode",
                SiteName = "Telegram",
                Title = "🎁 Telegram Premium Gift",
                Description = "Open this link to redeem your Telegram Premium gift code."
            },
            Users = [],
            Chats = []
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

    private static long GetLong(BsonDocument? doc, string field)
    {
        if (doc == null || !doc.Contains(field) || doc[field].IsBsonNull) return 0;
        return doc[field].IsInt64 ? doc[field].AsInt64 : doc[field].AsInt32;
    }

    private static int GetInt(BsonDocument? doc, string field)
    {
        if (doc == null || !doc.Contains(field) || doc[field].IsBsonNull) return 0;
        return doc[field].IsInt32 ? doc[field].AsInt32 : (int)doc[field].AsInt64;
    }

    private static int? GetNullableInt(BsonDocument? doc, string field)
    {
        if (doc == null || !doc.Contains(field) || doc[field].IsBsonNull) return null;
        return doc[field].IsInt32 ? doc[field].AsInt32 : (int)doc[field].AsInt64;
    }

    private static long? GetNullableLong(BsonDocument? doc, string field)
    {
        if (doc == null || !doc.Contains(field) || doc[field].IsBsonNull) return null;
        return doc[field].IsInt64 ? doc[field].AsInt64 : doc[field].AsInt32;
    }

    private static string? GetNullableString(BsonDocument? doc, string field)
    {
        if (doc == null || !doc.Contains(field) || doc[field].IsBsonNull) return null;
        return doc[field].AsString;
    }

    private static int? ResolveOfferMinStars(BsonDocument savedGiftDoc, BsonDocument? giftDoc)
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
